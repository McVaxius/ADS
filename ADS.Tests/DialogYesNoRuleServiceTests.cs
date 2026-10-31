using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Xunit;

namespace ADS.Tests;

public sealed class DialogYesNoRuleServiceTests
{
    private static IPluginLog Log() => DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
    private static DialogYesNoRuleManifest Manifest(string pattern, bool enabled = true) => new()
    {
        Rules = [new() { PromptPattern = pattern, MatchMode = "Exact", Enabled = enabled }],
    };

    [Fact]
    public void SelectedCustomRulesAndDisabledChoicesSurviveConfigurationRoundTrip()
    {
        using var directory = new TempDirectory();
        var config = JsonConvert.DeserializeObject<Configuration>("{}")!;
        Assert.Equal("DEFAULT", config.ActiveDialogRulePreset);
        string? saved = null;
        var service = new DialogYesNoRuleService(Log(), directory.Path, config,
            () => saved = JsonConvert.SerializeObject(config));
        Assert.True(service.SaveManifest(Manifest("Default prompt")));
        Assert.True(service.SaveManifest("Personal", Manifest("Default prompt", false)));
        Assert.True(service.TryActivatePreset("Personal", out _));
        Assert.Null(service.MatchRule("Default prompt"));
        Assert.Equal(0, service.ActiveRuleCount);

        Assert.NotNull(saved);
        var restored = JsonConvert.DeserializeObject<Configuration>(saved)!;
        var reloaded = new DialogYesNoRuleService(Log(), directory.Path, restored);
        Assert.Equal("Personal", restored.ActiveDialogRulePreset);
        Assert.Equal("Personal", reloaded.ActivePresetName);
        Assert.False(Assert.Single(reloaded.Current.Rules).Enabled);
        Assert.Null(reloaded.MatchRule("Default prompt"));
        Assert.Equal(service.ConfigPath, reloaded.ConfigPath);
        Assert.NotEqual(reloaded.ConfigPath, reloaded.ActivePresetPath);
    }

    [Fact]
    public void SavingSelectedCustomPresetAppliesImmediatelyWhileOtherSavesLeaveRuntimeAlone()
    {
        using var directory = new TempDirectory();
        var service = new DialogYesNoRuleService(Log(), directory.Path, new Configuration());
        Assert.True(service.SaveManifest("Personal", Manifest("First")));
        Assert.True(service.TryActivatePreset("Personal", out _));
        Assert.True(service.SaveManifest("Personal", Manifest("Edited")));
        Assert.NotNull(service.MatchRule("Edited"));
        Assert.Null(service.MatchRule("First"));
        Assert.True(service.SaveManifest(Manifest("Updated default")));
        Assert.True(service.SaveManifest("Other", Manifest("Other prompt")));
        Assert.Equal("Personal", service.ActivePresetName);
        Assert.NotNull(service.MatchRule("Edited"));
        Assert.Null(service.MatchRule("Updated default"));
        Assert.Null(service.MatchRule("Other prompt"));
    }

    [Fact]
    public void DiskPollingWatchesSelectedCustomFileRatherThanDefaultCache()
    {
        using var directory = new TempDirectory();
        var service = new DialogYesNoRuleService(Log(), directory.Path, new Configuration());
        Assert.True(service.SaveManifest("Personal", Manifest("Custom")));
        Assert.True(service.TryActivatePreset("Personal", out _));
        File.WriteAllText(service.ConfigPath, System.Text.Json.JsonSerializer.Serialize(Manifest("Remote")));
        Assert.False(service.ReloadIfChanged());
        Assert.NotNull(service.MatchRule("Custom"));

        Assert.True(service.TryActivatePreset("Personal", out _)); // Begin a fresh normal poll interval.
        var changed = File.GetLastWriteTimeUtc(service.ActivePresetPath).AddSeconds(2);
        File.WriteAllText(service.ActivePresetPath, System.Text.Json.JsonSerializer.Serialize(Manifest("Disk edit")));
        File.SetLastWriteTimeUtc(service.ActivePresetPath, changed);
        Assert.True(service.ReloadIfChanged());
        Assert.NotNull(service.MatchRule("Disk edit"));
        Assert.Null(service.MatchRule("Remote"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidSelectedCustomFileDoesNotActivateOrCopyDefault(bool invalid)
    {
        using var directory = new TempDirectory();
        var config = new Configuration();
        var service = new DialogYesNoRuleService(Log(), directory.Path, config);
        Assert.True(service.SaveManifest(Manifest("Default prompt")));
        Assert.True(service.SaveManifest("Personal", Manifest("Custom")));
        Assert.True(service.TryActivatePreset("Personal", out _));
        if (invalid) File.WriteAllText(service.ActivePresetPath, "{ invalid JSON");
        else File.Delete(service.ActivePresetPath);

        Assert.False(service.Reload());
        var reloaded = new DialogYesNoRuleService(Log(), directory.Path, config);
        Assert.Equal("Personal", reloaded.ActivePresetName);
        Assert.Equal("Personal", config.ActiveDialogRulePreset);
        Assert.Empty(reloaded.Current.Rules);
        Assert.Null(reloaded.MatchRule("Default prompt"));
        Assert.Equal(invalid, File.Exists(reloaded.ActivePresetPath));
        Assert.False(reloaded.TryLoadManifest("Missing", out _, out _));
        Assert.False(File.Exists(reloaded.GetPresetPath("Missing")));
    }

    [Fact]
    public void FailedActivationPreservesCurrentRuntimeAndSavedSelection()
    {
        using var directory = new TempDirectory();
        var config = new Configuration();
        var service = new DialogYesNoRuleService(Log(), directory.Path, config,
            () => throw new IOException("Synthetic config write failure"));
        Assert.True(service.SaveManifest(Manifest("Default prompt")));
        Assert.True(service.SaveManifest("Personal", Manifest("Custom")));
        Assert.False(service.TryActivatePreset("Missing", out _));
        Assert.False(File.Exists(service.GetPresetPath("Missing")));
        Assert.False(service.TryActivatePreset("Personal", out var status));
        Assert.Contains("Synthetic config write failure", status);
        Assert.Equal("DEFAULT", service.ActivePresetName);
        Assert.Equal("DEFAULT", config.ActiveDialogRulePreset);
        Assert.NotNull(service.MatchRule("Default prompt"));
    }

    [Fact]
    public void ExplicitDeletionOfSelectedCustomPresetPersistsDefaultSelection()
    {
        using var directory = new TempDirectory();
        var config = new Configuration();
        var saves = 0;
        var service = new DialogYesNoRuleService(Log(), directory.Path, config, () => saves++);
        Assert.True(service.SaveManifest(Manifest("Default prompt")));
        Assert.True(service.SaveManifest("Personal", Manifest("Custom")));
        Assert.True(service.TryActivatePreset("Personal", out _));
        Assert.True(service.TryDeletePreset("Personal", out _));
        Assert.Equal("DEFAULT", service.ActivePresetName);
        Assert.Equal("DEFAULT", config.ActiveDialogRulePreset);
        Assert.Equal(2, saves);
        Assert.NotNull(service.MatchRule("Default prompt"));
        Assert.False(File.Exists(service.GetPresetPath("Personal")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentDialogOwnerCancelsPendingAutomaticResponsesAndReportsTheCorrectOwner(bool raid)
    {
        using var directory = new TempDirectory();
        var service = new DialogYesNoRuleService(Log(), directory.Path, new Configuration());
        var automation = new DialogAutomationService(
            DispatchProxy.Create<IGameGui, AdsRulePrecedenceTests.NoOpProxy>(), service, Log());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DialogAutomationService).GetField("selectYesNoResponsePending", flags)!.SetValue(automation, true);
        typeof(DialogAutomationService).GetField("pendingSelectYesNoActionKey", flags)!.SetValue(automation, "synthetic-return-rule");
        automation.Update(TestDutyContextFactory.Create(), OwnershipMode.OwnedStartInside,
            pluginEnabled: true, processDialogRulesOutsideOwnedDuty: false, suppressGenericYesNo: true,
            suppressionReason: raid ? "Raid mode owns recovery dialogs; Return is manual" : null);
        Assert.False((bool)typeof(DialogAutomationService).GetField("selectYesNoResponsePending", flags)!.GetValue(automation)!);
        Assert.Empty(automation.DialogLastAction);
        Assert.Empty(automation.DialogRule);
        Assert.Contains(raid ? "Raid mode owns recovery dialogs; Return is manual" : "utility repair owns SelectYesno confirmations",
            automation.DialogStatus);
        if (raid)
            Assert.DoesNotContain("utility repair", automation.DialogStatus);
    }

    [Fact]
    public void ChangingManifestCancelsPreviousRuleResponseBeforeItCanRetryOrClaimSuccess()
    {
        using var directory = new TempDirectory();
        var service = new DialogYesNoRuleService(Log(), directory.Path, new Configuration());
        Assert.True(service.SaveManifest("Personal", Manifest("Prompt")));
        Assert.True(service.TryActivatePreset("Personal", out _));
        var automation = new DialogAutomationService(
            DispatchProxy.Create<IGameGui, AdsRulePrecedenceTests.NoOpProxy>(), service, Log());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DialogAutomationService).GetField("selectYesNoResponsePending", flags)!.SetValue(automation, true);
        typeof(DialogAutomationService).GetField("pendingSelectYesNoActionKey", flags)!.SetValue(automation, "previous-rule");
        Assert.True(service.SaveManifest("Personal", Manifest("Prompt", false)));
        var context = new DutyContextSnapshot
        {
            PluginEnabled = true, IsLoggedIn = true, BoundByDuty = false, BoundByDuty56 = false,
            BetweenAreas = false, BetweenAreas51 = false, Jumping = false, Jumping61 = false,
            Occupied33 = false, OccupiedInQuestEvent = false, OccupiedInEvent = false,
            OccupiedInCutSceneEvent = false, WatchingCutscene = false, InCombat = false, Mounted = false,
            TerritoryTypeId = 0, MapId = 0, ContentFinderConditionId = 0, CurrentDuty = null,
        };
        typeof(DialogAutomationService).GetMethod("TryHandleDialogRules", flags)!
            .Invoke(automation, [context, DateTime.UtcNow]);
        Assert.False((bool)typeof(DialogAutomationService).GetField("selectYesNoResponsePending", flags)!.GetValue(automation)!);
        Assert.Empty(automation.DialogLastAction);
        Assert.Empty(automation.DialogRule);
    }
}
