using ADS.Models;
using ADS.Windows;
using ADS.Localization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using AethertekUI;

namespace ADS.Tests;

[CollectionDefinition("ADS guide native UI", DisableParallelization = true)]
public sealed class GuideNativeUiCollection;

[Collection("ADS guide native UI")]
public sealed class WizardCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void NativeGuidesAndDebugSettingsRenderAtBothDensitiesAndGuideOffersDismissWithEscape(bool compact)
    {
        var previous = ImGui.GetCurrentContext();
        var language = Ui.Language;
        var context = ImGui.CreateContext();
        try
        {
            Ui.SetLanguage(UiLanguage.English);
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.LogFilename = null;
            io.DisplaySize = new(1280, 900);
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            byte* pixels = null;
            int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            io.Fonts.SetTexID(0, new ImTextureID((nint)1));
            var theme = AdsPresentation.Theme(AdsPresentation.ReferenceAccent);
            theme.Density = compact ? MaterialDensity.Compact : MaterialDensity.Standard;
            using var themeScope = MaterialTheme.Push(theme, 1, MaterialStyleMode.ColorsOnly);
            var drawOffer = typeof(PositionedWindow).GetMethod("DrawToolGuideOffer", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var tool in new[] { ToolGuide.Rules, ToolGuide.ObjectExplorer })
            {
                var configuration = new Configuration();
                var saves = 0;
                var guideOpens = 0;
                object[] args = [configuration, (Action)(() => ++saves), tool, true, false, (Action)(() => ++guideOpens)];
                for (var frame = 0; frame < 5; frame++)
                {
                    if (frame == 2) io.AddKeyEvent(ImGuiKey.Escape, true);
                    if (frame == 3) io.AddKeyEvent(ImGuiKey.Escape, false);
                    ImGui.NewFrame();
                    ImGui.SetNextWindowSize(compact ? new(760, 560) : new(1100, 800));
                    ImGui.Begin("ADS actual guide offer test", ImGuiWindowFlags.NoSavedSettings);
                    try { drawOffer.Invoke(null, args); }
                    finally { ImGui.End(); }
                    ImGui.Render();
                }
                Assert.False(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
                Assert.Equal(1, saves);
                Assert.Equal(0, guideOpens);
                Assert.False(configuration.RulesGuideVisited);
                Assert.False(configuration.ObjectExplorerGuideVisited);
                Assert.False(configuration.RulesDataWizardCompleted);
            }

            using var guide = new RuleGuideWindow(compact);
            var task = typeof(RuleGuideWindow).GetField("rulesTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var tool in new[] { ToolGuide.Rules, ToolGuide.ObjectExplorer })
            {
                guide.OpenForTool(tool);
                for (var selectedTask = 0; selectedTask < (tool == ToolGuide.Rules ? 6 : 1); selectedTask++)
                {
                    task.SetValue(guide, selectedTask);
                    for (var frame = 0; frame < 2; frame++)
                    {
                        ImGui.NewFrame();
                        ImGui.SetNextWindowSize(compact ? new(760, 560) : new(1100, 800));
                        ImGui.Begin(guide.WindowName, ImGuiWindowFlags.NoSavedSettings);
                        try { guide.Draw(); }
                        finally { ImGui.End(); }
                        ImGui.Render();
                        Assert.True(ImGui.GetDrawData().CmdListsCount > 0);
                    }
                }
            }
            foreach (var enabled in new[] { false, true })
            {
                var configuration = new Configuration
                {
                    DebugModeEnabled = enabled,
                    DebugReturnPolicy = DebugReturnPolicy.Resume,
                    ShowDebugSections = true,
                    FrameworkHitchProfilerEnabled = true,
                };
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowSize(compact ? new(640, 740) : new(760, 860));
                    ImGui.Begin("ADS actual Debug settings test", ImGuiWindowFlags.NoSavedSettings);
                    var changed = false;
                    try { ConfigWindow.DrawDebugSettings(configuration, enabled, _ => Assert.Fail("Unexpected user toggle"), ref changed); }
                    finally { ImGui.End(); }
                    ImGui.Render();
                    Assert.False(changed);
                    Assert.Equal(DebugReturnPolicy.Resume, configuration.DebugReturnPolicy);
                    Assert.Equal(enabled, configuration.DebugModeEnabled);
                    Assert.True(configuration.ShowDebugSections);
                    Assert.True(configuration.FrameworkHitchProfilerEnabled);
                    Assert.True(ImGui.GetDrawData().CmdListsCount > 0);
                }
            }
        }
        finally
        {
            Ui.SetLanguage(language);
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void DebugReturnDefaultsAndNativeStoragePreserveExplicitPolicyAndIndependentDiagnostics()
    {
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("""{"Version":25}""")!;
        Assert.False(configuration.DebugModeEnabled);
        Assert.Equal(DebugReturnPolicy.RestartFromEntrance, configuration.DebugReturnPolicy);
        configuration.DebugReturnPolicy = DebugReturnPolicy.Resume;
        configuration.FrameworkHitchProfilerEnabled = true;
        configuration.ShowDebugSections = true;
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(configuration))!;
        Assert.False(restored.DebugModeEnabled);
        Assert.Equal(DebugReturnPolicy.Resume, restored.DebugReturnPolicy);
        Assert.True(restored.FrameworkHitchProfilerEnabled);
        Assert.True(restored.ShowDebugSections);
        Assert.Equal(25, restored.Version);
        restored.DebugReturnPolicy = (DebugReturnPolicy)99;
        Assert.Equal(DebugReturnPolicy.RestartFromEntrance, restored.DebugReturnPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ToolGuideOfferDismissalAndActualVisitsRemainIndependent(int toolIndex)
    {
        var tool = (ToolGuide)toolIndex;
        var other = tool == ToolGuide.Rules ? ToolGuide.ObjectExplorer : ToolGuide.Rules;
        var configuration = new Configuration { WizardHubSeen = true, RulesDataWizardCompleted = true };
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, other));
        Assert.True(WizardCatalog.ConsumeToolGuideOffer(configuration, tool)); // Dismiss, close or Escape.
        Assert.False(WizardCatalog.ConsumeToolGuideOffer(configuration, tool));
        Assert.False(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, other));
        Assert.False(configuration.RulesGuideVisited);
        Assert.False(configuration.ObjectExplorerGuideVisited);
        Assert.True(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        Assert.False(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        Assert.True(tool == ToolGuide.Rules ? configuration.RulesGuideVisited : configuration.ObjectExplorerGuideVisited);
        Assert.True(configuration.RulesDataWizardCompleted);
        Assert.False(configuration.DutyOperationsWizardCompleted);
        Assert.False(configuration.UtilitiesWizardCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ManualToolGuideVisitSuppressesTheOfferAndPersistsWithoutCompletingAWizard(int toolIndex)
    {
        var tool = (ToolGuide)toolIndex;
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            """{"Version":25,"WizardHubSeen":true,"UiWindowOpacityPercent":73}""")!;
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(configuration))!;
        Assert.False(WizardCatalog.ShouldOfferToolGuide(restored, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(restored, tool == ToolGuide.Rules ? ToolGuide.ObjectExplorer : ToolGuide.Rules));
        Assert.False(restored.RulesDataWizardCompleted);
        Assert.False(restored.DiagnosticsRecoveryWizardCompleted);
        Assert.Equal(73, restored.UiWindowOpacityPercent);
        Assert.Equal(25, restored.Version);
    }

    [Fact]
    public void CompactDefaultsMigrateOnceThroughNativeJsonAndPreserveLaterOverrides()
    {
        var storage = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.PluginConfigurations", true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var serialize = storage.GetMethod("SerializeConfig", flags)!;
        var deserialize = storage.GetMethod("DeserializeConfig", flags)!;
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            """{"Version":24,"UiCompact":false,"UiCompactVisibleOnMainWindow":true,"UiTransparencyVisibleOnMainWindow":true,"UiWindowOpacityPercent":73,"EnableBmraiVbmInRegularDuties":false,"FutureSetting":{"value":[1,"unchanged"]}}""")!;
        Assert.True(config.UiShowTitlebarStatus);
        Assert.True(config.UiDetailedTitlebarStatus);
        Assert.True(Plugin.ApplyConfigurationMigrations(config));
        Assert.True(config.UiCompact);
        Assert.False(config.UiCompactVisibleOnMainWindow);
        Assert.False(config.UiTransparencyVisibleOnMainWindow);
        Assert.True(config.UiCompactDefaultsApplied);
        config.UiCompact = false;
        config.UiShowTitlebarStatus = config.UiDetailedTitlebarStatus = false;
        config.UiCompactVisibleOnMainWindow = config.UiTransparencyVisibleOnMainWindow = true;
        for (var reload = 0; reload < 2; reload++)
        {
            var saved = (string)serialize.Invoke(null, [config])!;
            config = (Configuration)deserialize.Invoke(null, [saved])!;
            Assert.False(config.ApplyCompactDefaults());
            Plugin.ApplyConfigurationMigrations(config); // Other existing normalizers are independent of the appearance marker.
            Assert.False(config.UiCompact);
            Assert.False(config.UiShowTitlebarStatus);
            Assert.False(config.UiDetailedTitlebarStatus);
            Assert.True(config.UiCompactVisibleOnMainWindow);
            Assert.True(config.UiTransparencyVisibleOnMainWindow);
            Assert.Equal(73, config.UiWindowOpacityPercent);
            Assert.False(config.EnableBmraiVbmInRegularDuties);
            Assert.Equal("unchanged", config.AdditionalSettings!["FutureSetting"]["value"]![1]!.ToObject<string>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedDisabledStateRecoversAndSerializesEnabledWithoutChangingFollow(bool follow)
    {
        var configuration = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            """{"Version":24,"PluginEnabled":false}""")!;
        configuration.EnableBmraiVbmInRegularDuties = follow;

        Assert.True(configuration.PluginEnabled);
        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.False(Plugin.ApplyConfigurationMigrations(configuration));
        configuration.PluginEnabled = false; // The retained compatibility setter cannot disable ADS.
        Assert.True(configuration.PluginEnabled);
        Assert.Equal(follow, configuration.EnableBmraiVbmInRegularDuties);
        using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(configuration));
        Assert.True(saved.RootElement.GetProperty("PluginEnabled").GetBoolean());
    }

    [Fact]
    public void CatalogHasStableIdsOrderAndThreeRequiredPages()
    {
        Assert.Equal(
            [
                WizardCatalog.DutyOperationsId,
                WizardCatalog.RulesDataId,
                WizardCatalog.UtilitiesId,
                WizardCatalog.TreasureFollowId,
                WizardCatalog.DiagnosticsRecoveryId,
            ],
            WizardCatalog.All.Select(wizard => wizard.Id));
        Assert.All(WizardCatalog.All, wizard =>
        {
            Assert.Equal(["overview", "safety", "steps"], wizard.Pages.Select(page => page.Id));
            Assert.All(wizard.Pages, page => Assert.False(string.IsNullOrWhiteSpace(page.Body)));
        });
        var rules = WizardCatalog.All.Single(wizard => wizard.Id == WizardCatalog.RulesDataId);
        var rulesText = string.Join(' ', rules.Pages.SelectMany(page => page.Steps.Prepend(page.Body).Prepend(page.Title)));
        Assert.Contains("combined effective", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete replacement", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-context promotion", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkout", rulesText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletionFlagsAreIndependentAndCompletedFlowsRemainCatalogued()
    {
        var configuration = new Configuration();

        WizardCatalog.SetCompleted(configuration, WizardCatalog.UtilitiesId);

        Assert.True(WizardCatalog.IsCompleted(configuration, WizardCatalog.UtilitiesId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.DutyOperationsId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.RulesDataId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.TreasureFollowId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.DiagnosticsRecoveryId));
        Assert.Contains(WizardCatalog.All, wizard => wizard.Id == WizardCatalog.UtilitiesId);
    }

    [Fact]
    public void NewInstallAutoOpensOnceAfterBeingMarkedSeen()
    {
        var configuration = new Configuration();

        Assert.True(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: false, configuration));
        configuration.WizardHubSeen = true;
        Assert.False(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: false, configuration));
    }

    [Fact]
    public void VersionTwentyMigrationMarksHubSeenWithoutCompletingFlows()
    {
        var configuration = new Configuration
        {
            Version = 20,
            WizardHubSeen = false,
            DutyOperationsWizardCompleted = true,
            RulesDataWizardCompleted = true,
            UtilitiesWizardCompleted = true,
            TreasureFollowWizardCompleted = true,
            DiagnosticsRecoveryWizardCompleted = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));

        Assert.Equal(25, configuration.Version);
        Assert.True(configuration.WizardHubSeen);
        Assert.False(configuration.DutyOperationsWizardCompleted);
        Assert.False(configuration.RulesDataWizardCompleted);
        Assert.False(configuration.UtilitiesWizardCompleted);
        Assert.False(configuration.TreasureFollowWizardCompleted);
        Assert.False(configuration.DiagnosticsRecoveryWizardCompleted);
        Assert.False(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: true, configuration));
    }

    [Fact]
    public void CurrentSchemaContextDefaultsNormalizeWithoutVersionBump()
    {
        var configuration = new Configuration
        {
            Version = 25,
            ObjectRuleSelectedContextFileNames = ["bad.json", "1037_rule_objects.json"],
            ObjectRuleEditorCompactMode = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));

        Assert.Equal(25, configuration.Version);
        Assert.Equal(["1037_rule_objects.json"], configuration.ObjectRuleSelectedContextFileNames);
        Assert.True(configuration.ObjectRuleEditorCompactMode);
    }

    [Fact]
    public void HigherLowerDiagnosticsResetOffOnceAndPreserveLaterSavedChoice()
    {
        var configuration = new Configuration
        {
            Version = 24,
            HigherLowerDiagnosticsEnabled = true,
            HigherLowerAutomationEnabled = true,
            HigherLowerVfxDataminingEnabled = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.Equal(25, configuration.Version);
        Assert.False(configuration.HigherLowerDiagnosticsEnabled);
        Assert.True(configuration.HigherLowerAutomationEnabled);
        Assert.True(configuration.HigherLowerVfxDataminingEnabled);

        configuration.HigherLowerDiagnosticsEnabled = true;
        Assert.False(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.True(configuration.HigherLowerDiagnosticsEnabled);

        var storage = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.PluginConfigurations", true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var saved = (string)storage.GetMethod("SerializeConfig", flags)!.Invoke(null, [configuration])!;
        var reloaded = (Configuration)storage.GetMethod("DeserializeConfig", flags)!.Invoke(null, [saved])!;

        Plugin.ApplyConfigurationMigrations(reloaded); // Other normalizers are independent of this migration.
        Assert.Equal(25, reloaded.Version);
        Assert.True(reloaded.HigherLowerDiagnosticsEnabled);
        Assert.True(reloaded.HigherLowerAutomationEnabled);
        Assert.True(reloaded.HigherLowerVfxDataminingEnabled);
    }
}
