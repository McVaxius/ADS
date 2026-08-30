using System.Reflection;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class RemoteJsonUpdateServiceTests
{
    [Fact]
    public void SharedRemoteFilesIncludeMatureProposalMirror()
        => Assert.Contains(RemoteJsonUpdateService.MatureProposalRulesFileName, RemoteJsonUpdateService.RemoteCacheFileNames);

    [Fact]
    public void FreshSharedRemoteFilesSkipRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromHours(23)))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.False(decision.ShouldRefresh);
        Assert.Empty(decision.MissingFiles);
        Assert.Empty(decision.StaleFiles);
        Assert.Equal("cache files are younger than 24h", decision.Status);
    }

    [Fact]
    public void MissingSharedRemoteFilesRequestRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, false, DateTime.MinValue))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.True(decision.ShouldRefresh);
        Assert.Equal(RemoteJsonUpdateService.RemoteCacheFileNames, decision.MissingFiles);
        Assert.Empty(decision.StaleFiles);
        Assert.Contains(RemoteJsonUpdateService.DutyMaturityFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.TreasureRoutesFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.MatureProposalRulesFileName, decision.Status);
    }

    [Fact]
    public void StaleSharedRemoteFilesRequestRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromHours(25)))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.True(decision.ShouldRefresh);
        Assert.Empty(decision.MissingFiles);
        Assert.Equal(RemoteJsonUpdateService.RemoteCacheFileNames, decision.StaleFiles);
        Assert.Contains(RemoteJsonUpdateService.DutyMaturityFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.TreasureRoutesFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.MatureProposalRulesFileName, decision.Status);
    }

    [Fact]
    public void MissingMatureProposalEditablePresetSeedsImmediately()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var decision = DecideMatureProposalSync(now, presetExists: false);

        Assert.True(decision.ShouldApply);
        Assert.Equal(MatureProposalSyncReason.PresetMissing, decision.Reason);
    }

    [Fact]
    public void NewerMatureProposalMirrorDefersForTwentyFourHours()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var presetWriteUtc = now - TimeSpan.FromHours(3);

        var decision = DecideMatureProposalSync(now, presetWriteUtc: presetWriteUtc);

        Assert.False(decision.ShouldApply);
        Assert.True(decision.IsPending);
        Assert.Equal(presetWriteUtc + TimeSpan.FromHours(24), decision.NextResetUtc);
        Assert.Equal(MatureProposalSyncReason.ProtectionActive, decision.Reason);
    }

    [Fact]
    public void MatureProposalMirrorAppliesAtProtectionBoundary()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var decision = DecideMatureProposalSync(now, presetWriteUtc: now - TimeSpan.FromHours(24));

        Assert.True(decision.ShouldApply);
        Assert.Equal(MatureProposalSyncReason.ProtectionExpired, decision.Reason);
    }

    [Fact]
    public void ForcedMatureProposalResetIgnoresProtectionWindow()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var decision = DecideMatureProposalSync(now, presetWriteUtc: now - TimeSpan.FromMinutes(1), force: true);

        Assert.True(decision.ShouldApply);
        Assert.Equal(MatureProposalSyncReason.Forced, decision.Reason);
    }

    [Fact]
    public void MatureProposalRemoteRefreshUsesEditablePresetWriteTime()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var fresh = RemoteJsonUpdateService.DecideMatureProposalRefresh(
            true,
            now - TimeSpan.FromHours(2),
            now,
            RemoteJsonUpdateService.RefreshInterval,
            force: false);
        var expired = RemoteJsonUpdateService.DecideMatureProposalRefresh(
            true,
            now - TimeSpan.FromHours(24),
            now,
            RemoteJsonUpdateService.RefreshInterval,
            force: false);
        var forced = RemoteJsonUpdateService.DecideMatureProposalRefresh(
            true,
            now - TimeSpan.FromMinutes(1),
            now,
            RemoteJsonUpdateService.RefreshInterval,
            force: true);

        Assert.False(fresh.ShouldRefresh);
        Assert.Equal(now + TimeSpan.FromHours(22), fresh.NextRefreshUtc);
        Assert.True(expired.ShouldRefresh);
        Assert.True(forced.ShouldRefresh);
    }

    [Fact]
    public void CompletedUpdateContractCarriesOnlyChangedFilesAndForceIntent()
    {
        var completion = new RemoteJsonUpdateCompletion(
            [RemoteJsonUpdateService.MatureProposalRulesFileName],
            ForceMatureProposalApply: true);

        Assert.Equal([RemoteJsonUpdateService.MatureProposalRulesFileName], completion.ChangedFiles);
        Assert.True(completion.ForceMatureProposalApply);
    }

    [Fact]
    public void CompletedUpdateReloadsOnlyAffectedFiles()
    {
        var steps = Plugin.BuildRemoteJsonReloadSteps(new RemoteJsonUpdateCompletion(
            [
                RemoteJsonUpdateService.ObjectRulesFileName,
                RemoteJsonUpdateService.MatureProposalRulesFileName,
                "unrecognized.json",
            ],
            ForceMatureProposalApply: true));

        Assert.Equal(
            [Plugin.RemoteJsonReloadStep.ObjectRules, Plugin.RemoteJsonReloadStep.MatureProposalRulesForced],
            steps);
    }

    [Fact]
    public void EditableMatureProposalNewerThanMirrorDoesNotReset()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var decision = ObjectPriorityRuleService.DecideMatureProposalSync(
            mirrorExists: true,
            mirrorWriteUtc: now - TimeSpan.FromHours(2),
            presetExists: true,
            presetWriteUtc: now - TimeSpan.FromHours(1),
            utcNow: now,
            protectionInterval: ObjectPriorityRuleService.MatureProposalProtectionInterval,
            force: false);

        Assert.False(decision.ShouldApply);
        Assert.False(decision.IsPending);
        Assert.Null(decision.NextResetUtc);
        Assert.Equal(MatureProposalSyncReason.EditableCurrent, decision.Reason);
    }

    [Fact]
    public void ExpiredMatureProposalPresetRefreshesFromNewMirror()
    {
        using var directory = new TempDirectory();
        var presetDirectory = Path.Combine(directory.Path, "rule-presets");
        Directory.CreateDirectory(presetDirectory);
        var presetPath = Path.Combine(presetDirectory, $"{ObjectPriorityRuleService.MatureProposalsPresetName}.json");
        var customManifest = new ObjectPriorityRuleManifest { Description = "keep me" };
        File.WriteAllText(presetPath, JsonSerializer.Serialize(customManifest));
        File.SetLastWriteTimeUtc(presetPath, DateTime.UtcNow - TimeSpan.FromDays(7));

        var log = DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
        var service = new ObjectPriorityRuleService(log, null!, directory.Path);
        var mirrorPath = service.MatureProposalsMirrorPath;
        File.WriteAllText(mirrorPath, JsonSerializer.Serialize(new ObjectPriorityRuleManifest { Description = "clean mirror" }));
        File.SetLastWriteTimeUtc(mirrorPath, DateTime.UtcNow);

        var applied = service.TrySynchronizeMatureProposals(force: false, out var status);

        Assert.True(applied);
        Assert.Contains("Applied", status);
        Assert.Equal("clean mirror", JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(File.ReadAllText(presetPath))!.Description);
    }

    private static MatureProposalSyncDecision DecideMatureProposalSync(
        DateTime now,
        bool presetExists = true,
        DateTime? presetWriteUtc = null,
        bool force = false)
        => ObjectPriorityRuleService.DecideMatureProposalSync(
            mirrorExists: true,
            mirrorWriteUtc: now,
            presetExists: presetExists,
            presetWriteUtc: presetWriteUtc ?? now - TimeSpan.FromHours(1),
            utcNow: now,
            protectionInterval: ObjectPriorityRuleService.MatureProposalProtectionInterval,
            force: force);
}
