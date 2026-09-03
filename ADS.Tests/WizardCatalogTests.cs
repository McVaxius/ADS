using ADS.Models;

namespace ADS.Tests;

public sealed class WizardCatalogTests
{
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

        Assert.Equal(24, configuration.Version);
        Assert.True(configuration.WizardHubSeen);
        Assert.False(configuration.DutyOperationsWizardCompleted);
        Assert.False(configuration.RulesDataWizardCompleted);
        Assert.False(configuration.UtilitiesWizardCompleted);
        Assert.False(configuration.TreasureFollowWizardCompleted);
        Assert.False(configuration.DiagnosticsRecoveryWizardCompleted);
        Assert.False(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: true, configuration));
    }

    [Fact]
    public void Schema24ContextDefaultsNormalizeWithoutVersionBump()
    {
        var configuration = new Configuration
        {
            Version = 24,
            ObjectRuleSelectedContextFileNames = ["bad.json", "1037_rule_objects.json"],
            ObjectRuleEditorCompactMode = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));

        Assert.Equal(24, configuration.Version);
        Assert.Equal(["1037_rule_objects.json"], configuration.ObjectRuleSelectedContextFileNames);
        Assert.True(configuration.ObjectRuleEditorCompactMode);
    }
}
