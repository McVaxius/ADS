using System.Diagnostics;
using System.Text.Json;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ObjectRulePromotionServiceTests
{
    private static readonly DutyCatalogEntry[] Catalog =
    [RemoteJsonUpdateServiceTests.Duty(2, 1037, "the Tam-Tara Deepcroft")];

    [Fact]
    public void CheckoutValidationResolvesRepositoryRootAndTerritoriesFolderToSameRoot()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, []);
        var service = new ObjectRulePromotionService(Catalog);

        var rootValid = service.TryValidateCheckout(checkout.Path, out var rootFromRepository, out var rootStatus);
        var territoriesValid = service.TryValidateCheckout(territories, out var rootFromTerritories, out var territoriesStatus);

        Assert.True(rootValid, rootStatus);
        Assert.True(territoriesValid, territoriesStatus);
        Assert.Equal(rootFromRepository, rootFromTerritories, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(Git(checkout.Path, "rev-parse", "--show-toplevel").Trim()), rootFromRepository, ignoreCase: true);
    }

    [Fact]
    public void CheckoutValidationTrimsWhitespaceAndOuterQuotes()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, []);
        var service = new ObjectRulePromotionService(Catalog);

        var valid = service.TryValidateCheckout($"  \"{territories}\"  ", out var root, out var status);

        Assert.True(valid, status);
        Assert.Equal(Path.GetFullPath(Git(checkout.Path, "rev-parse", "--show-toplevel").Trim()), root, ignoreCase: true);
    }

    [Fact]
    public void CheckoutValidationReportsMissingAndNonGitFolders()
    {
        using var directory = new TempDirectory();
        var service = new ObjectRulePromotionService(Catalog);
        var missingPath = Path.Combine(directory.Path, "missing");

        Assert.False(service.TryValidateCheckout(missingPath, out _, out var missingStatus));
        Assert.Equal($"The checkout path does not exist: {Path.GetFullPath(missingPath)}.", missingStatus);

        Assert.False(service.TryValidateCheckout(directory.Path, out _, out var nonGitStatus));
        Assert.Contains("Git validation failed", nonGitStatus);
    }

    [Fact]
    public void CheckoutValidationRejectsIncorrectLayoutsAndUnsupportedSubfolders()
    {
        using var wrongLayout = new TempDirectory();
        Git(wrongLayout.Path, "init");
        var service = new ObjectRulePromotionService(Catalog);

        Assert.False(service.TryValidateCheckout(wrongLayout.Path, out _, out var layoutStatus));
        Assert.Equal("The checkout is missing the expected ads/territories layout.", layoutStatus);

        using var checkout = new TempDirectory();
        InitializeCheckout(checkout.Path, []);
        var unsupported = Path.Combine(checkout.Path, "ads");

        Assert.False(service.TryValidateCheckout(unsupported, out _, out var unsupportedStatus));
        Assert.Equal("Choose either the BotologyUpdates repository root or its ads/territories folder.", unsupportedStatus);
    }

    [Fact]
    public void CheckoutValidationReportsInvalidIndex()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, []);
        File.WriteAllText(Path.Combine(territories, ObjectRuleShardStore.IndexFileName), "not json");
        var service = new ObjectRulePromotionService(Catalog);

        Assert.False(service.TryValidateCheckout(checkout.Path, out _, out var status));
        Assert.StartsWith("Invalid territory index:", status);
    }

    [Fact]
    public void SharedCheckoutStateKeepsInvalidCandidateSeparateFromSavedCanonicalRoot()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, []);
        var canonicalRoot = Path.GetFullPath(Git(checkout.Path, "rev-parse", "--show-toplevel").Trim());
        var configuration = new Configuration { BotologyUpdatesCheckoutPath = canonicalRoot };
        var saveCount = 0;
        var settingsState = new ObjectRuleCheckoutState(
            new ObjectRulePromotionService(Catalog),
            configuration,
            () => saveCount++);
        var objectRulesState = settingsState;

        Assert.True(settingsState.IsValid, settingsState.Status);
        Assert.True(objectRulesState.IsValid);
        Assert.Equal(canonicalRoot, objectRulesState.ConfiguredRoot, ignoreCase: true);

        var missingPath = Path.Combine(checkout.Path, "missing");
        settingsState.SetCandidatePath(missingPath);
        Assert.False(objectRulesState.IsValid);
        Assert.Equal(missingPath, objectRulesState.CandidatePath);

        Assert.False(objectRulesState.TryUseCheckout());
        Assert.Equal($"The checkout path does not exist: {Path.GetFullPath(missingPath)}.", settingsState.Status);
        Assert.Equal(canonicalRoot, configuration.BotologyUpdatesCheckoutPath, ignoreCase: true);
        Assert.Equal(canonicalRoot, settingsState.ConfiguredRoot, ignoreCase: true);
        Assert.Equal(0, saveCount);

        objectRulesState.SetCandidatePath($"\"{territories}\"");
        Assert.True(settingsState.TryUseCheckout(), settingsState.Status);
        Assert.True(objectRulesState.IsValid);
        Assert.Equal(canonicalRoot, objectRulesState.CandidatePath, ignoreCase: true);
        Assert.Equal(canonicalRoot, configuration.BotologyUpdatesCheckoutPath, ignoreCase: true);
        Assert.Equal(1, saveCount);

        objectRulesState.SetValidationFailure("Invalid territory index: changed after checkout activation.");
        Assert.False(settingsState.IsValid);
        Assert.Equal("Invalid territory index: changed after checkout activation.", settingsState.Status);
        Assert.Equal(canonicalRoot, settingsState.ConfiguredRoot, ignoreCase: true);

        Assert.True(settingsState.TryUseCheckout(), settingsState.Status);
        Assert.True(objectRulesState.IsValid);
        Assert.Equal(2, saveCount);

        settingsState.Clear();
        Assert.False(objectRulesState.IsValid);
        Assert.Empty(objectRulesState.CandidatePath);
        Assert.Empty(objectRulesState.ConfiguredRoot);
        Assert.Empty(configuration.BotologyUpdatesCheckoutPath);
        Assert.Equal("No BotologyUpdates checkout is configured.", objectRulesState.Status);
        Assert.Equal(3, saveCount);
    }

    [Fact]
    public void PromotionCopiesWholeSavedContextWithoutChangingExistingIndexOrGitState()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        var targetPath = Path.Combine(territories, "1037_rule_objects.json");
        WriteShard(targetPath, "old");
        CommitBaseline(checkout.Path);
        var branch = Git(checkout.Path, "branch", "--show-current").Trim();

        using var source = new TempDirectory();
        var sourcePath = Path.Combine(source.Path, "1037_rule_objects.json");
        WriteShard(sourcePath, "visible", "hidden-by-text-filter");
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(checkout.Path, sourcePath, "1037_rule_objects.json", overwriteConfirmed: false);

        Assert.True(result.Success, result.Status);
        Assert.False(result.NoOp);
        Assert.Equal(["visible", "hidden-by-text-filter"], ReadShard(targetPath).Rules.Select(rule => rule.ObjectName));
        Assert.Equal(["1037_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Equal(branch, Git(checkout.Path, "branch", "--show-current").Trim());
        Assert.Empty(Git(checkout.Path, "diff", "--cached"));
        var status = Git(checkout.Path, "status", "--porcelain");
        Assert.Contains("1037_rule_objects.json", status);
        Assert.DoesNotContain("index.json", status);
    }

    [Fact]
    public void PromotionAddsOnlyANewCanonicalContextAndIndexEntry()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, [ObjectRuleShardStore.GlobalFileName]);
        WriteShard(Path.Combine(territories, ObjectRuleShardStore.GlobalFileName), "global", territory: 0);
        CommitBaseline(checkout.Path);

        using var source = new TempDirectory();
        var sourcePath = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(sourcePath, "custom-only", territory: 9000);
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(checkout.Path, sourcePath, "9000_rule_objects.json", overwriteConfirmed: false);

        Assert.True(result.Success, result.Status);
        Assert.Equal([ObjectRuleShardStore.GlobalFileName, "9000_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Empty(Git(checkout.Path, "diff", "--cached"));
        var changed = Git(checkout.Path, "status", "--porcelain");
        Assert.Contains("9000_rule_objects.json", changed);
        Assert.Contains("index.json", changed);
    }

    [Fact]
    public void PromotionRequiresConfirmationBeforeOverwritingLocalTargetChanges()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        var targetPath = Path.Combine(territories, "1037_rule_objects.json");
        WriteShard(targetPath, "baseline");
        CommitBaseline(checkout.Path);
        WriteShard(targetPath, "local-change");

        using var source = new TempDirectory();
        var sourcePath = Path.Combine(source.Path, "1037_rule_objects.json");
        WriteShard(sourcePath, "incoming");
        var service = new ObjectRulePromotionService(Catalog);

        var blocked = service.Promote(checkout.Path, sourcePath, "1037_rule_objects.json", overwriteConfirmed: false);

        Assert.False(blocked.Success);
        Assert.True(blocked.RequiresOverwriteConfirmation);
        Assert.Equal("local-change", ReadShard(targetPath).Rules.Single().ObjectName);

        var confirmed = service.Promote(checkout.Path, sourcePath, "1037_rule_objects.json", overwriteConfirmed: true);

        Assert.True(confirmed.Success, confirmed.Status);
        Assert.Equal("incoming", ReadShard(targetPath).Rules.Single().ObjectName);
        Assert.Equal(["1037_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Empty(Git(checkout.Path, "diff", "--cached"));
    }

    [Fact]
    public void BatchPromotionCopiesCompleteContextsAndUpdatesIndexForAllNewContexts()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        WriteShard(Path.Combine(territories, "1037_rule_objects.json"), "old");
        CommitBaseline(checkout.Path);
        var branch = Git(checkout.Path, "branch", "--show-current").Trim();

        using var source = new TempDirectory();
        var firstSource = Path.Combine(source.Path, "1037_rule_objects.json");
        var secondSource = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(firstSource, "visible", "hidden-by-filter");
        WriteShard(secondSource, "custom-only", territory: 9000);
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(
            checkout.Path,
            [
                new ObjectRulePromotionSource("1037_rule_objects.json", firstSource),
                new ObjectRulePromotionSource("9000_rule_objects.json", secondSource),
            ],
            overwriteConfirmed: false);

        Assert.True(result.Success, result.Status);
        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], result.ChangedContexts);
        Assert.Empty(result.NoOpContexts);
        Assert.Equal(["visible", "hidden-by-filter"], ReadShard(Path.Combine(territories, "1037_rule_objects.json")).Rules.Select(rule => rule.ObjectName));
        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Equal(branch, Git(checkout.Path, "branch", "--show-current").Trim());
        Assert.Empty(Git(checkout.Path, "diff", "--cached"));
    }

    [Fact]
    public void BatchPromotionReportsNoOpAndEmptyContextsSeparately()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        var target = Path.Combine(territories, "1037_rule_objects.json");
        WriteShard(target, "same");
        CommitBaseline(checkout.Path);

        using var source = new TempDirectory();
        var sameSource = Path.Combine(source.Path, "1037_rule_objects.json");
        var emptySource = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(sameSource, "same");
        WriteShard(emptySource, [], 9000);
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(
            checkout.Path,
            [
                new ObjectRulePromotionSource("1037_rule_objects.json", sameSource),
                new ObjectRulePromotionSource("9000_rule_objects.json", emptySource),
            ],
            overwriteConfirmed: false);

        Assert.True(result.Success, result.Status);
        Assert.Equal(["9000_rule_objects.json"], result.ChangedContexts);
        Assert.Equal(["1037_rule_objects.json"], result.NoOpContexts);
        Assert.Equal(["9000_rule_objects.json"], result.EmptyContexts);
        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Empty(ReadShard(Path.Combine(territories, "9000_rule_objects.json")).Rules);
    }

    [Fact]
    public void BatchPromotionPrevalidatesEverySourceBeforeWritingAnything()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        var target = Path.Combine(territories, "1037_rule_objects.json");
        WriteShard(target, "before");
        CommitBaseline(checkout.Path);

        using var source = new TempDirectory();
        var validSource = Path.Combine(source.Path, "1037_rule_objects.json");
        var invalidSource = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(validSource, "after");
        File.WriteAllText(invalidSource, "not json");
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(
            checkout.Path,
            [
                new ObjectRulePromotionSource("1037_rule_objects.json", validSource),
                new ObjectRulePromotionSource("9000_rule_objects.json", invalidSource),
            ],
            overwriteConfirmed: false);

        Assert.False(result.Success);
        Assert.Equal("before", ReadShard(target).Rules.Single().ObjectName);
        Assert.Equal(["1037_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Empty(Git(checkout.Path, "status", "--porcelain"));
    }

    [Fact]
    public void BatchPromotionPrevalidatesEveryExistingDestinationBeforeWritingAnything()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json", "9000_rule_objects.json"]);
        var firstTarget = Path.Combine(territories, "1037_rule_objects.json");
        WriteShard(firstTarget, "before");
        File.WriteAllText(Path.Combine(territories, "9000_rule_objects.json"), "not json");
        CommitBaseline(checkout.Path);

        using var source = new TempDirectory();
        var firstSource = Path.Combine(source.Path, "1037_rule_objects.json");
        var secondSource = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(firstSource, "after");
        WriteShard(secondSource, "valid", territory: 9000);
        var service = new ObjectRulePromotionService(Catalog);

        var result = service.Promote(
            checkout.Path,
            [
                new ObjectRulePromotionSource("1037_rule_objects.json", firstSource),
                new ObjectRulePromotionSource("9000_rule_objects.json", secondSource),
            ],
            overwriteConfirmed: false);

        Assert.False(result.Success);
        Assert.Contains("destination is invalid", result.Status);
        Assert.Equal("before", ReadShard(firstTarget).Rules.Single().ObjectName);
        Assert.Empty(Git(checkout.Path, "status", "--porcelain"));
    }

    [Fact]
    public void BatchPromotionRequestsOneConfirmationForChangesAcrossShardAndIndexPaths()
    {
        using var checkout = new TempDirectory();
        var territories = InitializeCheckout(checkout.Path, ["1037_rule_objects.json"]);
        var target = Path.Combine(territories, "1037_rule_objects.json");
        var indexPath = Path.Combine(territories, ObjectRuleShardStore.IndexFileName);
        WriteShard(target, "baseline");
        CommitBaseline(checkout.Path);
        WriteShard(target, "local-target");
        File.AppendAllText(indexPath, Environment.NewLine);

        using var source = new TempDirectory();
        var changedSource = Path.Combine(source.Path, "1037_rule_objects.json");
        var newSource = Path.Combine(source.Path, "9000_rule_objects.json");
        WriteShard(changedSource, "incoming");
        WriteShard(newSource, "new", territory: 9000);
        var sources = new[]
        {
            new ObjectRulePromotionSource("1037_rule_objects.json", changedSource),
            new ObjectRulePromotionSource("9000_rule_objects.json", newSource),
        };
        var service = new ObjectRulePromotionService(Catalog);

        var blocked = service.Promote(checkout.Path, sources, overwriteConfirmed: false);

        Assert.False(blocked.Success);
        Assert.True(blocked.RequiresOverwriteConfirmation);
        Assert.Contains("1037_rule_objects.json", blocked.Status);
        Assert.Contains("index.json", blocked.Status);
        Assert.Equal("local-target", ReadShard(target).Rules.Single().ObjectName);

        var confirmed = service.Promote(checkout.Path, sources, overwriteConfirmed: true);

        Assert.True(confirmed.Success, confirmed.Status);
        Assert.Equal("incoming", ReadShard(target).Rules.Single().ObjectName);
        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], ReadIndex(territories).Files);
        Assert.Empty(Git(checkout.Path, "diff", "--cached"));
    }

    private static string InitializeCheckout(string root, IReadOnlyList<string> files)
    {
        Git(root, "init");
        Git(root, "config", "user.email", "ads-tests@example.invalid");
        Git(root, "config", "user.name", "ADS Tests");
        var territories = Path.Combine(root, "ads", "territories");
        Directory.CreateDirectory(territories);
        ObjectRuleShardStore.WriteIndexAtomic(Path.Combine(territories, ObjectRuleShardStore.IndexFileName), new ObjectPriorityRuleShardIndex
        {
            Files = files.ToList(),
        });
        return territories;
    }

    private static void CommitBaseline(string root)
    {
        Git(root, "add", "ads/territories");
        Git(root, "commit", "-m", "baseline");
    }

    private static void WriteShard(string path, params string[] names)
        => WriteShard(path, names, 1037);

    private static void WriteShard(string path, string name, uint territory)
        => WriteShard(path, [name], territory);

    private static void WriteShard(string path, IReadOnlyList<string> names, uint territory)
    {
        var rules = names.Select(name => territory == 0
            ? new ObjectPriorityRule { ObjectName = name }
            : new ObjectPriorityRule
            {
                TerritoryTypeId = territory,
                ContentFinderConditionId = territory == 1037 ? 2u : 0u,
                DutyEnglishName = territory == 1037 ? "the Tam-Tara Deepcroft" : string.Empty,
                ObjectName = name,
            }).ToList();
        ObjectRuleShardStore.WriteJsonAtomic(path, JsonSerializer.Serialize(new ObjectPriorityRuleManifest { Rules = rules }));
    }

    private static ObjectPriorityRuleManifest ReadShard(string path)
        => JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(File.ReadAllText(path))!;

    private static ObjectPriorityRuleShardIndex ReadIndex(string territories)
        => JsonSerializer.Deserialize<ObjectPriorityRuleShardIndex>(File.ReadAllText(Path.Combine(territories, ObjectRuleShardStore.IndexFileName)))!;

    private static string Git(string workingDirectory, params string[] arguments)
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
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
