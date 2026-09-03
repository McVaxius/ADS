using System.Text.Json;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class RemoteJsonUpdateServiceTests
{
    [Fact]
    public void SharedRemoteFilesUseTerritoryIndexAndExcludeLegacyMonoliths()
    {
        Assert.Contains(RemoteJsonUpdateService.TerritoriesIndexFileName, RemoteJsonUpdateService.RemoteCacheFileNames);
        Assert.DoesNotContain("duty-object-rules.json", RemoteJsonUpdateService.RemoteCacheFileNames);
        Assert.DoesNotContain("duty-object-rules-mature-proposals.json", RemoteJsonUpdateService.RemoteCacheFileNames);
    }

    [Fact]
    public void FreshSharedRemoteFilesSkipRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromHours(23)));

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.False(decision.ShouldRefresh);
        Assert.Equal("territory index is younger than 24h and all shared files are present", decision.Status);
    }

    [Fact]
    public void TerritoryIndexAloneControlsAgeBasedRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames.Select(fileName =>
            new RemoteJsonCacheFileState(
                fileName,
                true,
                now - (fileName == RemoteJsonUpdateService.TerritoriesIndexFileName
                    ? TimeSpan.FromHours(23)
                    : TimeSpan.FromDays(10))));

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.False(decision.ShouldRefresh);
        Assert.Equal("territory index is younger than 24h and all shared files are present", decision.Status);
    }

    [Fact]
    public void FreshInvalidTerritoryIndexStillForcesRefresh()
    {
        using var directory = new TempDirectory();
        var territories = Path.Combine(directory.Path, ObjectRuleShardStore.DirectoryName);
        Directory.CreateDirectory(territories);
        File.WriteAllText(Path.Combine(territories, ObjectRuleShardStore.IndexFileName), "not json");
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromMinutes(1)));

        var cacheState = RemoteJsonUpdateService.InspectLocalObjectRuleCache(directory.Path, []);
        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval, cacheState);

        Assert.False(cacheState.IsValid);
        Assert.True(decision.ShouldRefresh);
        Assert.Contains("invalid object-rule cache", decision.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshMissingOrInvalidIndexedShardStillForcesRefresh(bool createInvalidShard)
    {
        using var directory = new TempDirectory();
        var territories = Path.Combine(directory.Path, ObjectRuleShardStore.DirectoryName);
        Directory.CreateDirectory(territories);
        ObjectRuleShardStore.WriteIndexAtomic(
            Path.Combine(territories, ObjectRuleShardStore.IndexFileName),
            new ObjectPriorityRuleShardIndex { Files = ["1037_rule_objects.json"] });
        if (createInvalidShard)
            File.WriteAllText(Path.Combine(territories, "1037_rule_objects.json"), "not json");
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromMinutes(1)));

        var cacheState = RemoteJsonUpdateService.InspectLocalObjectRuleCache(directory.Path, [Duty(2, 1037, "the Tam-Tara Deepcroft")]);
        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval, cacheState);

        Assert.False(cacheState.IsValid);
        Assert.Contains("territories/1037_rule_objects.json", cacheState.ProblemFiles);
        Assert.True(decision.ShouldRefresh);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"Files\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Files\":[\"1037_rule_objects.json\",\"1037_rule_objects.json\"]}")]
    [InlineData("{\"SchemaVersion\":1,\"Files\":[\"../1037_rule_objects.json\"]}")]
    [InlineData("{\"SchemaVersion\":1,\"Files\":[\"1037.json\"]}")]
    [InlineData("{\"SchemaVersion\":1,\"Files\":[\"1037_rule_objects.json\",\"GLOBAL_rule_objects.json\"]}")]
    public void RemoteIndexRejectsUnsupportedUnsafeDuplicateAndUnsortedEntries(string indexJson)
        => Assert.False(RemoteJsonUpdateService.TryValidateObjectRulePackage(
            indexJson,
            new Dictionary<string, string>(),
            [],
            out _,
            out _));

    [Fact]
    public void RemotePackageRejectsMissingAndMixedContextShards()
    {
        var catalog = new[] { Duty(2, 1037, "the Tam-Tara Deepcroft") };
        const string indexJson = "{\"SchemaVersion\":1,\"Files\":[\"GLOBAL_rule_objects.json\",\"1037_rule_objects.json\"]}";
        var globalJson = JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Rules = [new ObjectPriorityRule { ObjectName = "global" }],
        });

        Assert.False(RemoteJsonUpdateService.TryValidateObjectRulePackage(
            indexJson,
            new Dictionary<string, string> { [ObjectRuleShardStore.GlobalFileName] = globalJson },
            catalog,
            out _,
            out _));

        var mixed = JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Rules = [new ObjectPriorityRule { TerritoryTypeId = 1037, ContentFinderConditionId = 2, DutyEnglishName = "the Tam-Tara Deepcroft" }],
        });
        Assert.False(RemoteJsonUpdateService.TryValidateObjectRulePackage(
            indexJson,
            new Dictionary<string, string>
            {
                [ObjectRuleShardStore.GlobalFileName] = mixed,
                ["1037_rule_objects.json"] = mixed,
            },
            catalog,
            out _,
            out _));
    }

    [Fact]
    public void CompletedTerritoryFilesQueueOneObjectRuleReload()
    {
        var steps = Plugin.BuildRemoteJsonReloadSteps(new RemoteJsonUpdateCompletion(
            true,
            false,
            ["territories/1037_rule_objects.json", "territories/index.json", "unrecognized.json"]));

        Assert.Equal([Plugin.RemoteJsonReloadStep.ObjectRules], steps);
    }

    internal static DutyCatalogEntry Duty(uint cfc, uint territory, string name)
        => new()
        {
            ContentFinderConditionId = cfc,
            TerritoryTypeId = territory,
            Name = name,
            EnglishName = name,
            ContentTypeName = "Dungeon",
            ExpansionName = "ARR",
            SupportNote = string.Empty,
            LevelRequired = 1,
            SortKey = 1,
            ExVersion = 0,
            ContentTypeRowId = 2,
            ContentMemberTypeRowId = 4,
            PartySize = 4,
            Category = DutyCategory.FourMan,
            SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
            IsMainScenario = false,
        };
}
