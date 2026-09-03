using System.Diagnostics;
using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

internal sealed class ObjectRulePromotionService(IReadOnlyList<DutyCatalogEntry> dutyCatalog)
{
    private const string RepositoryTerritoriesPath = "ads/territories";

    public bool TryValidateCheckout(string candidatePath, out string rootPath, out string status)
    {
        rootPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            status = "Choose an existing BotologyUpdates checkout folder.";
            return false;
        }

        string normalizedCandidate;
        try
        {
            normalizedCandidate = candidatePath.Trim();
            if (normalizedCandidate.Length >= 2
                && ((normalizedCandidate[0] == '"' && normalizedCandidate[^1] == '"')
                    || (normalizedCandidate[0] == '\'' && normalizedCandidate[^1] == '\'')))
            {
                normalizedCandidate = normalizedCandidate[1..^1].Trim();
            }
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                status = "Choose an existing BotologyUpdates checkout folder.";
                return false;
            }
            normalizedCandidate = Path.GetFullPath(normalizedCandidate);
        }
        catch (Exception ex)
        {
            status = $"The checkout path is invalid: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedCandidate))
        {
            status = $"The checkout path does not exist: {normalizedCandidate}.";
            return false;
        }
        if (!TryRunGit(normalizedCandidate, ["rev-parse", "--show-toplevel"], out var output, out status))
            return false;
        if (!TryRunGit(normalizedCandidate, ["rev-parse", "--show-prefix"], out var prefixOutput, out status))
            return false;
        try
        {
            var discovered = output.Trim();
            if (string.IsNullOrWhiteSpace(discovered) || !Directory.Exists(discovered))
            {
                status = "Git did not return a valid checkout root.";
                return false;
            }
            rootPath = Path.GetFullPath(discovered);
            var territoriesPath = Path.Combine(rootPath, "ads", "territories");
            var prefix = prefixOutput.Trim().Replace('\\', '/').TrimEnd('/');
            if (prefix.Length > 0 && !string.Equals(prefix, RepositoryTerritoriesPath, StringComparison.OrdinalIgnoreCase))
            {
                status = $"Choose either the BotologyUpdates repository root or its {RepositoryTerritoriesPath} folder.";
                rootPath = string.Empty;
                return false;
            }
            var indexPath = Path.Combine(territoriesPath, ObjectRuleShardStore.IndexFileName);
            if (!Directory.Exists(territoriesPath) || !File.Exists(indexPath))
            {
                status = $"The checkout is missing the expected {RepositoryTerritoriesPath} layout.";
                rootPath = string.Empty;
                return false;
            }
            if (!ObjectRuleShardStore.TryParseIndexJson(File.ReadAllText(indexPath), out _, out status))
            {
                rootPath = string.Empty;
                return false;
            }
            status = $"Validated BotologyUpdates checkout at {rootPath}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to inspect the BotologyUpdates checkout: {ex.Message}";
            rootPath = string.Empty;
            return false;
        }
    }

    public ObjectRulePromotionResult Promote(
        string checkoutPath,
        string sourceShardPath,
        string fileName,
        bool overwriteConfirmed)
        => Promote(
            checkoutPath,
            [new ObjectRulePromotionSource(fileName, sourceShardPath)],
            overwriteConfirmed);

    public ObjectRulePromotionResult Promote(
        string checkoutPath,
        IReadOnlyList<ObjectRulePromotionSource> sources,
        bool overwriteConfirmed)
    {
        if (!TryValidateCheckout(checkoutPath, out var rootPath, out var status))
            return ObjectRulePromotionResult.Failed(string.Empty, status);
        if (sources.Count == 0)
            return ObjectRulePromotionResult.Failed(rootPath, "Select at least one saved custom context override to promote.");
        if (sources.Select(source => source.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            return ObjectRulePromotionResult.Failed(rootPath, "The promotion selection contains duplicate context filenames.");

        var affectedPaths = new List<string>();
        try
        {
            var territoriesPath = Path.GetFullPath(Path.Combine(rootPath, "ads", "territories"));
            var expectedPrefix = territoriesPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var indexPath = Path.Combine(territoriesPath, ObjectRuleShardStore.IndexFileName);
            if (!ObjectRuleShardStore.TryParseIndexJson(File.ReadAllText(indexPath), out var index, out status))
                return ObjectRulePromotionResult.Failed(rootPath, status);

            var prepared = new List<PreparedPromotion>();
            foreach (var source in sources)
            {
                if (!ObjectRuleShardStore.TryParseCanonicalFileName(source.FileName, out _))
                    return ObjectRulePromotionResult.Failed(rootPath, $"Invalid promotion context filename {source.FileName}.");
                if (!File.Exists(source.SourceShardPath))
                    return ObjectRulePromotionResult.Failed(rootPath, $"Saved custom override is missing: {source.SourceShardPath}.");

                var sourceJson = File.ReadAllText(source.SourceShardPath);
                if (!ObjectRuleShardStore.TryValidateShardJson(sourceJson, source.FileName, dutyCatalog, out var sourceManifest, out status))
                    return ObjectRulePromotionResult.Failed(rootPath, status);

                var targetPath = Path.GetFullPath(Path.Combine(territoriesPath, source.FileName));
                if (!targetPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                    return ObjectRulePromotionResult.Failed(rootPath, "Promotion destination escaped the verified territories folder.");

                string? destinationJson = null;
                if (File.Exists(targetPath))
                {
                    destinationJson = File.ReadAllText(targetPath);
                    if (!ObjectRuleShardStore.TryValidateShardJson(destinationJson, source.FileName, dutyCatalog, out _, out status))
                        return ObjectRulePromotionResult.Failed(rootPath, $"Existing promotion destination is invalid. {status}");
                }

                prepared.Add(new PreparedPromotion(
                    source.FileName,
                    sourceJson,
                    targetPath,
                    string.Equals(destinationJson, sourceJson, StringComparison.Ordinal),
                    index.Files.Contains(source.FileName, StringComparer.Ordinal),
                    sourceManifest.Rules.Count == 0));
            }

            var changedContexts = prepared.Where(item => !item.DestinationMatches || !item.IsIndexed).Select(item => item.FileName).ToList();
            var noOpContexts = prepared.Where(item => item.DestinationMatches && item.IsIndexed).Select(item => item.FileName).ToList();
            affectedPaths.AddRange(prepared.Where(item => !item.DestinationMatches).Select(item => item.TargetPath));
            var addToIndex = prepared.Where(item => !item.IsIndexed).Select(item => item.FileName).ToList();
            if (addToIndex.Count > 0)
                affectedPaths.Add(indexPath);

            if (affectedPaths.Count == 0)
            {
                return ObjectRulePromotionResult.Completed(
                    rootPath,
                    [],
                    [],
                    noOpContexts,
                    prepared.Where(item => item.IsEmpty).Select(item => item.FileName).ToList(),
                    $"Promotion complete. Changed: none. No-op contexts: {string.Join(", ", noOpContexts)}.");
            }

            var relativePaths = affectedPaths
                .Select(path => Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            if (!TryRunGit(rootPath, ["status", "--porcelain", "--", .. relativePaths], out var gitStatus, out status))
                return ObjectRulePromotionResult.Failed(rootPath, status, affectedPaths);
            var locallyChanged = gitStatus.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
                .ToList();
            if (locallyChanged.Count > 0 && !overwriteConfirmed)
            {
                return ObjectRulePromotionResult.OverwriteRequired(
                    rootPath,
                    affectedPaths,
                    changedContexts,
                    noOpContexts,
                    prepared.Where(item => item.IsEmpty).Select(item => item.FileName).ToList(),
                    $"Promotion would overwrite local changes in: {string.Join(", ", locallyChanged)}.");
            }

            foreach (var item in prepared.Where(item => !item.DestinationMatches))
                ObjectRuleShardStore.WriteJsonAtomic(item.TargetPath, item.SourceJson);
            if (addToIndex.Count > 0)
            {
                index.Files.AddRange(addToIndex);
                ObjectRuleShardStore.WriteIndexAtomic(indexPath, index);
            }
            return ObjectRulePromotionResult.Completed(
                rootPath,
                affectedPaths,
                changedContexts,
                noOpContexts,
                prepared.Where(item => item.IsEmpty).Select(item => item.FileName).ToList(),
                $"Promotion complete. Changed contexts: {string.Join(", ", changedContexts)}. No-op contexts: {(noOpContexts.Count == 0 ? "none" : string.Join(", ", noOpContexts))}. Git was not otherwise modified.");
        }
        catch (Exception ex)
        {
            return ObjectRulePromotionResult.Failed(rootPath, $"Promotion failed: {ex.Message}", affectedPaths);
        }
    }

    private static bool TryRunGit(string workingDirectory, IReadOnlyList<string> arguments, out string output, out string status)
    {
        output = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                status = "Git could not be started.";
                return false;
            }
            output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                status = string.IsNullOrWhiteSpace(error) ? "Git validation failed." : $"Git validation failed: {error}";
                return false;
            }
            status = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            status = $"Git validation failed: {ex.Message}";
            return false;
        }
    }

    private sealed record PreparedPromotion(
        string FileName,
        string SourceJson,
        string TargetPath,
        bool DestinationMatches,
        bool IsIndexed,
        bool IsEmpty);
}

internal sealed class ObjectRuleCheckoutState
{
    private const string NotConfiguredStatus = "No BotologyUpdates checkout is configured.";
    private readonly ObjectRulePromotionService promotionService;
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private string observedConfiguredPath;
    private string configuredRoot = string.Empty;

    public ObjectRuleCheckoutState(
        ObjectRulePromotionService promotionService,
        Configuration configuration,
        Action saveConfiguration)
    {
        this.promotionService = promotionService;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        observedConfiguredPath = configuration.BotologyUpdatesCheckoutPath;
        CandidatePath = observedConfiguredPath;
        LoadConfiguredPath();
    }

    public string CandidatePath { get; private set; }
    public string ConfiguredRoot => configuredRoot;
    public string Status { get; private set; } = NotConfiguredStatus;
    public bool IsValid { get; private set; }

    public void RefreshFromConfiguration()
    {
        if (string.Equals(observedConfiguredPath, configuration.BotologyUpdatesCheckoutPath, StringComparison.Ordinal))
            return;

        observedConfiguredPath = configuration.BotologyUpdatesCheckoutPath;
        CandidatePath = observedConfiguredPath;
        LoadConfiguredPath();
    }

    public void SetCandidatePath(string candidatePath)
    {
        if (string.Equals(CandidatePath, candidatePath, StringComparison.Ordinal))
            return;

        CandidatePath = candidatePath;
        IsValid = false;
        Status = "Path changed; use the checkout before promotion.";
    }

    public bool TryUseCheckout()
    {
        IsValid = promotionService.TryValidateCheckout(CandidatePath, out var root, out var status);
        Status = status;
        if (!IsValid)
            return false;

        CandidatePath = root;
        configuredRoot = root;
        configuration.BotologyUpdatesCheckoutPath = root;
        observedConfiguredPath = root;
        saveConfiguration();
        return true;
    }

    public void SetValidationFailure(string status)
    {
        IsValid = false;
        Status = status;
    }

    public void Clear()
    {
        CandidatePath = string.Empty;
        configuredRoot = string.Empty;
        IsValid = false;
        Status = NotConfiguredStatus;
        configuration.BotologyUpdatesCheckoutPath = string.Empty;
        observedConfiguredPath = string.Empty;
        saveConfiguration();
    }

    private void LoadConfiguredPath()
    {
        if (string.IsNullOrWhiteSpace(observedConfiguredPath))
        {
            configuredRoot = string.Empty;
            IsValid = false;
            Status = NotConfiguredStatus;
            return;
        }

        IsValid = promotionService.TryValidateCheckout(observedConfiguredPath, out var root, out var status);
        Status = status;
        if (!IsValid)
        {
            configuredRoot = string.Empty;
            return;
        }

        CandidatePath = root;
        configuredRoot = root;
        if (string.Equals(configuration.BotologyUpdatesCheckoutPath, root, StringComparison.Ordinal))
            return;

        configuration.BotologyUpdatesCheckoutPath = root;
        observedConfiguredPath = root;
        saveConfiguration();
    }
}

internal sealed record ObjectRulePromotionSource(string FileName, string SourceShardPath);

internal sealed record ObjectRulePromotionResult(
    bool Success,
    bool RequiresOverwriteConfirmation,
    string CheckoutRoot,
    IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<string> ChangedContexts,
    IReadOnlyList<string> NoOpContexts,
    IReadOnlyList<string> EmptyContexts,
    string Status)
{
    public bool NoOp => Success && ChangedContexts.Count == 0;

    public static ObjectRulePromotionResult Failed(string root, string status, IReadOnlyList<string>? affectedPaths = null)
        => new(false, false, root, affectedPaths ?? [], [], [], [], status);

    public static ObjectRulePromotionResult OverwriteRequired(
        string root,
        IReadOnlyList<string> affectedPaths,
        IReadOnlyList<string> changedContexts,
        IReadOnlyList<string> noOpContexts,
        IReadOnlyList<string> emptyContexts,
        string status)
        => new(false, true, root, affectedPaths, changedContexts, noOpContexts, emptyContexts, status);

    public static ObjectRulePromotionResult Completed(
        string root,
        IReadOnlyList<string> affectedPaths,
        IReadOnlyList<string> changedContexts,
        IReadOnlyList<string> noOpContexts,
        IReadOnlyList<string> emptyContexts,
        string status)
        => new(true, false, root, affectedPaths, changedContexts, noOpContexts, emptyContexts, status);
}
