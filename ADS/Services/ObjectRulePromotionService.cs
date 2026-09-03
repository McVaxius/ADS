using System.Diagnostics;
using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

internal sealed class ObjectRulePromotionService(IReadOnlyList<DutyCatalogEntry> dutyCatalog)
{
    private const string RepositoryTerritoriesPath = "ads/territories";

    public bool TryDiscoverCheckoutFromCurrentDirectory(out string rootPath, out string status)
        => TryValidateCheckout(Environment.CurrentDirectory, out rootPath, out status);

    public bool TryValidateCheckout(string candidatePath, out string rootPath, out string status)
    {
        rootPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath) || !Directory.Exists(candidatePath))
        {
            status = "Choose an existing BotologyUpdates checkout folder.";
            return false;
        }
        if (!TryRunGit(candidatePath, ["rev-parse", "--show-toplevel"], out var output, out status))
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
    {
        if (!TryValidateCheckout(checkoutPath, out var rootPath, out var status))
            return new ObjectRulePromotionResult(false, false, false, string.Empty, [], status);
        var affectedPaths = new List<string>();
        try
        {
            if (!ObjectRuleShardStore.TryParseCanonicalFileName(fileName, out _))
                return new ObjectRulePromotionResult(false, false, false, rootPath, [], $"Invalid promotion context filename {fileName}.");
            if (!File.Exists(sourceShardPath))
                return new ObjectRulePromotionResult(false, false, false, rootPath, [], $"Saved custom override is missing: {sourceShardPath}.");

            var sourceJson = File.ReadAllText(sourceShardPath);
            if (!ObjectRuleShardStore.TryValidateShardJson(sourceJson, fileName, dutyCatalog, out _, out status))
                return new ObjectRulePromotionResult(false, false, false, rootPath, [], status);

            var territoriesPath = Path.GetFullPath(Path.Combine(rootPath, "ads", "territories"));
            var targetPath = Path.GetFullPath(Path.Combine(territoriesPath, fileName));
            var expectedPrefix = territoriesPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                return new ObjectRulePromotionResult(false, false, false, rootPath, [], "Promotion destination escaped the verified territories folder.");

            var indexPath = Path.Combine(territoriesPath, ObjectRuleShardStore.IndexFileName);
            if (!ObjectRuleShardStore.TryParseIndexJson(File.ReadAllText(indexPath), out var index, out status))
                return new ObjectRulePromotionResult(false, false, false, rootPath, [], status);

            var destinationMatches = File.Exists(targetPath)
                                     && File.ReadAllText(targetPath) == sourceJson;
            var addToIndex = !index.Files.Contains(fileName, StringComparer.Ordinal);
            if (destinationMatches && !addToIndex)
            {
                return new ObjectRulePromotionResult(
                    true,
                    true,
                    false,
                    rootPath,
                    [targetPath],
                    $"No promotion changes were needed; {fileName} already matches the saved custom override.");
            }

            if (!destinationMatches)
                affectedPaths.Add(targetPath);
            if (addToIndex)
                affectedPaths.Add(indexPath);

            var relativePaths = affectedPaths
                .Select(path => Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            if (!TryRunGit(rootPath, ["status", "--porcelain", "--", .. relativePaths], out var gitStatus, out status))
                return new ObjectRulePromotionResult(false, false, false, rootPath, affectedPaths, status);
            var locallyChanged = gitStatus.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
                .ToList();
            if (locallyChanged.Count > 0 && !overwriteConfirmed)
            {
                return new ObjectRulePromotionResult(
                    false,
                    false,
                    true,
                    rootPath,
                    affectedPaths,
                    $"Promotion would overwrite local changes in: {string.Join(", ", locallyChanged)}.");
            }

            if (!destinationMatches)
                ObjectRuleShardStore.WriteJsonAtomic(targetPath, sourceJson);
            if (addToIndex)
            {
                index.Files.Add(fileName);
                ObjectRuleShardStore.WriteIndexAtomic(indexPath, index);
            }
            return new ObjectRulePromotionResult(
                true,
                false,
                false,
                rootPath,
                affectedPaths,
                $"Promoted the complete saved {fileName} context. Git was not otherwise modified.");
        }
        catch (Exception ex)
        {
            return new ObjectRulePromotionResult(false, false, false, rootPath, affectedPaths, $"Promotion failed: {ex.Message}");
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
}

internal sealed record ObjectRulePromotionResult(
    bool Success,
    bool NoOp,
    bool RequiresOverwriteConfirmation,
    string CheckoutRoot,
    IReadOnlyList<string> AffectedPaths,
    string Status);
