using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin.Services;

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

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task DownloadCompletionRestartsOnlySuccessfulManualUpdatesAfterAllReloads(
        bool manual, bool changed, bool failDownload)
    {
        using var directory = new TempDirectory();
        var log = DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
        var files = new Dictionary<string, string>
        {
            [RemoteJsonUpdateService.TerritoriesIndexFileName] = "{\"SchemaVersion\":1,\"Files\":[\"1037_rule_objects.json\"]}",
            ["territories/1037_rule_objects.json"] = JsonSerializer.Serialize(new ObjectPriorityRuleManifest
            {
                Rules = [new ObjectPriorityRule { TerritoryTypeId = 1037, ContentFinderConditionId = 2, ObjectName = "downloaded" }],
            }),
            [RemoteJsonUpdateService.DialogRulesFileName] = "{\"SchemaVersion\":1,\"Rules\":[]}",
            [RemoteJsonUpdateService.DutyMaturityFileName] = "{\"SchemaVersion\":1,\"Duties\":[]}",
            [RemoteJsonUpdateService.TreasureRoutesFileName] = "{\"SchemaVersion\":1,\"Routes\":[]}",
        };
        foreach (var (file, json) in files)
        {
            var path = Path.Combine(directory.Path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, changed ? json + "\n" : json);
        }

        using var updater = new RemoteJsonUpdateService(log, directory.Path, [Duty(2, 1037, "Test duty")]);
        var httpField = typeof(RemoteJsonUpdateService).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)httpField.GetValue(updater)!).Dispose();
        httpField.SetValue(updater, new HttpClient(new RemoteFilesHandler(files, failDownload)));
        var context = UpdateContext();
        var restart = RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.OwnedStartInside)!;
        Assert.True(manual
            ? updater.TryStartUpdate(true, "manual test", true, restart)
            : updater.TryStartUpdate(true, "automatic test"));
        await ((Task)typeof(RemoteJsonUpdateService).GetField("activeUpdateTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(updater)!).WaitAsync(TimeSpan.FromSeconds(10));
        var updateStatus = updater.LastUpdateStatus;

        Assert.True(updater.ShouldDeferLocalReloadPolling);
        // A second download must not overwrite a completion awaiting the framework tick.
        Assert.False(updater.TryStartUpdate(true, "second update before consumption"));
        Assert.True(updater.TryConsumeCompletedUpdate(out var completion));
        Assert.False(updater.TryConsumeCompletedUpdate(out _));
        Assert.True(completion.Success == !failDownload, updateStatus);
        Assert.Equal(manual, completion.ManualUpdate);
        Assert.Same(manual ? restart : null, completion.OwnedDutyRestart);
        Assert.Equal(changed && !failDownload ? files.Count : 0, completion.ChangedFiles.Count);

        var batch = new Plugin.RemoteJsonReloadBatch(completion);
        var calls = new List<string>();
        bool TryRestart() => batch.TryRestart(context, OwnershipMode.OwnedStartInside,
            () => calls.Add("Stop"), () => { calls.Add("Start Inside"); return true; });
        var expectedReloads = !failDownload && (manual || changed) ? 4 : 0;
        Assert.Equal(expectedReloads, batch.RemainingSteps);
        for (var i = 0; i < expectedReloads; i++)
        {
            Assert.False(TryRestart());
            batch.RunNext(step => { calls.Add(step.ToString()); return true; });
        }

        Assert.Equal(manual && !failDownload, TryRestart());
        Assert.False(TryRestart()); // The completion cannot restart twice.
        if (manual && !failDownload)
            Assert.Equal(["ObjectRules", "DialogRules", "DutyMaturity", "TreasureRoutes", "Stop", "Start Inside"], calls);
        else
            Assert.DoesNotContain("Stop", calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void AnyFailedOrThrowingReloadPreventsRestart(int failedStep, bool throws)
    {
        var context = UpdateContext();
        var batch = ManualBatch(context);
        var reloads = 0;
        while (batch.RemainingSteps > 0)
        {
            bool Reload(Plugin.RemoteJsonReloadStep _)
            {
                if (reloads++ != failedStep) return true;
                if (throws) throw new InvalidDataException("bad reload");
                return false;
            }
            if (throws && reloads == failedStep)
                Assert.Throws<InvalidDataException>(() => batch.RunNext(Reload));
            else
                batch.RunNext(Reload);
        }
        Assert.Equal(4, reloads);
        Assert.False(batch.TryRestart(context, OwnershipMode.OwnedStartInside,
            () => Assert.Fail("Unexpected Stop"), () => throw new Exception("Unexpected Start")));
    }

    [Theory]
    [InlineData("BetweenAreas")]
    [InlineData("BetweenAreas51")]
    [InlineData("SelectYesno")]
    public void DeferredManualReloadWaitsAndRestartsAfterFinalStep(string deferral)
    {
        var context = UpdateContext();
        var restart = RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.OwnedStartInside)!;
        var batch = ManualBatch(context, restart);
        var reloads = 0;
        var stops = 0;
        var starts = 0;
        void Tick(DutyContextSnapshot current, bool dialog)
        {
            restart.Observe(current, OwnershipMode.OwnedStartInside);
            if (Plugin.ShouldDeferJsonReloads(current, dialog, out _)) return;
            batch.RunNext(_ => { ++reloads; return true; });
            batch.TryRestart(current, OwnershipMode.OwnedStartInside, () => ++stops, () => { ++starts; return true; });
        }
        Tick(context, false);
        var transition = deferral == "SelectYesno" ? context : UpdateContext(
            inDuty: false, territory: 0, content: 0,
            betweenAreas: deferral == "BetweenAreas", betweenAreas51: deferral == "BetweenAreas51");
        Tick(transition, deferral == "SelectYesno");
        Tick(transition, deferral == "SelectYesno");
        Assert.Equal(1, reloads);
        Assert.Equal(0, stops);
        Assert.True(restart.IsPending);
        restart.ObserveTerritory(context.TerritoryTypeId); // A same-territory respawn keeps the request.
        Tick(context, false);
        Tick(context, false);
        Assert.Equal(0, starts);
        Tick(context, false);
        Tick(context, false);
        Assert.Equal(4, reloads);
        Assert.Equal(1, stops);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("leave")]
    [InlineData("outside")]
    [InlineData("logout")]
    [InlineData("territory")]
    [InlineData("content")]
    [InlineData("new run")]
    [InlineData("territory event")]
    public void LostOwnershipCannotRevivePendingRestartEvenAfterReturningToSameDuty(string cancellation)
    {
        var context = UpdateContext();
        var restart = RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.OwnedStartInside)!;
        var batch = ManualBatch(context, restart);
        switch (cancellation)
        {
            case "stop": restart.Observe(context, OwnershipMode.Observing); break;
            case "leave": restart.Observe(context, OwnershipMode.Leaving); break;
            case "outside": restart.Observe(UpdateContext(inDuty: false), OwnershipMode.OwnedStartOutside); break;
            case "logout": restart.Observe(UpdateContext(loggedIn: false, betweenAreas: true), OwnershipMode.OwnedStartInside); break;
            case "territory": restart.Observe(UpdateContext(territory: 1039), OwnershipMode.OwnedStartInside); break;
            case "content": restart.Observe(UpdateContext(content: 3), OwnershipMode.OwnedStartInside); break;
            case "new run": restart.Cancel(); break;
            case "territory event": restart.ObserveTerritory(1039); break;
        }
        while (batch.RemainingSteps > 0) batch.RunNext(_ => true);
        Assert.False(batch.TryRestart(context, OwnershipMode.OwnedStartInside,
            () => Assert.Fail("Unexpected Stop"), () => throw new Exception("Unexpected Start")));
        Assert.Null(RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.Observing));
        Assert.Null(RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.Leaving));
        Assert.Null(RemoteJsonOwnedDutyRestart.Capture(UpdateContext(inDuty: false), OwnershipMode.OwnedStartOutside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualReloadKeepsCustomPresetAndOverridesAndDoesNotRestartOnInvalidPreset(bool corruptOverride)
    {
        using var directory = new TempDirectory();
        var log = DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
        var configuration = new Configuration();
        File.WriteAllText(Path.Combine(directory.Path, ObjectRuleShardStore.LegacyFileName),
            JsonSerializer.Serialize(new ObjectPriorityRuleManifest { Rules = [new ObjectPriorityRule { ObjectName = "old inherited rule" }] }));
        var rules = new ObjectPriorityRuleService(log, null!, directory.Path, [], configuration, null, null);
        var custom = rules.CreateEditableCopy();
        custom.Rules.Add(new ObjectPriorityRule { TerritoryTypeId = 1037, ObjectName = "custom override" });
        Assert.True(rules.SaveManifest("Custom", custom), rules.LastLoadStatus);
        var overridePath = rules.GetContextShardPath("Custom", "1037_rule_objects.json");
        var overrideJson = File.ReadAllText(overridePath);
        if (corruptOverride) File.WriteAllText(overridePath, "invalid JSON");
        else File.WriteAllText(rules.GetContextShardPath("DEFAULT", "GLOBAL_rule_objects.json"),
            JsonSerializer.Serialize(new ObjectPriorityRuleManifest { Rules = [new ObjectPriorityRule { ObjectName = "new inherited rule" }] }));

        var context = UpdateContext();
        var batch = ManualBatch(context);
        while (batch.RemainingSteps > 0)
            batch.RunNext(step => step != Plugin.RemoteJsonReloadStep.ObjectRules || rules.ReloadAfterManualUpdate());
        var stops = 0;
        Assert.Equal(!corruptOverride, batch.TryRestart(context, OwnershipMode.OwnedStartInside, () => ++stops, () => true));
        Assert.Equal(corruptOverride ? 0 : 1, stops);
        Assert.Equal("Custom", rules.ActivePresetName);
        Assert.Equal("Custom", configuration.ActiveObjectRulePreset);
        Assert.Contains(rules.Current.Rules, rule => rule.ObjectName == "custom override");
        Assert.Equal(corruptOverride ? "invalid JSON" : overrideJson, File.ReadAllText(overridePath));
        if (!corruptOverride)
            Assert.Contains(rules.Current.Rules, rule => rule.ObjectName == "new inherited rule");
    }

    private static Plugin.RemoteJsonReloadBatch ManualBatch(DutyContextSnapshot context, RemoteJsonOwnedDutyRestart? restart = null)
        => new(new RemoteJsonUpdateCompletion(true, false, [], true)
        {
            OwnedDutyRestart = restart ?? RemoteJsonOwnedDutyRestart.Capture(context, OwnershipMode.OwnedStartInside),
        });

    private static DutyContextSnapshot UpdateContext(bool inDuty = true, bool loggedIn = true,
        bool betweenAreas = false, bool betweenAreas51 = false, uint territory = 1037, uint content = 2) => new()
    {
        PluginEnabled = true, IsLoggedIn = loggedIn, BoundByDuty = inDuty, BoundByDuty56 = false,
        BetweenAreas = betweenAreas, BetweenAreas51 = betweenAreas51, Jumping = false, Jumping61 = false,
        Occupied33 = false, OccupiedInQuestEvent = false, OccupiedInEvent = false,
        OccupiedInCutSceneEvent = false, WatchingCutscene = false, InCombat = false, Mounted = false,
        TerritoryTypeId = territory, MapId = 1, ContentFinderConditionId = content, CurrentDuty = null,
    };

    private sealed class RemoteFilesHandler(IReadOnlyDictionary<string, string> files, bool failDownload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var file = request.RequestUri!.AbsolutePath.Split("/ads/", 2)[1];
            return Task.FromResult(new HttpResponseMessage(failDownload ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(files[file]),
            });
        }
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
