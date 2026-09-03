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
