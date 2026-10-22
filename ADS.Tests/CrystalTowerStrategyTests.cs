using System.Reflection;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class CrystalTowerStrategyTests
{
    [Theory]
    [InlineData("SYNC", CrystalTowerStrategyMode.Sync)]
    [InlineData(" unsync ", CrystalTowerStrategyMode.Unsync)]
    [InlineData(null, CrystalTowerStrategyMode.None)]
    [InlineData("unknown", CrystalTowerStrategyMode.Invalid)]
    public void ExplicitStrategySurvivesEveryExistingCopyAndImportPath(string? mode, CrystalTowerStrategyMode expected)
    {
        using var directory = new TempDirectory();
        var context = Context();
        File.WriteAllText(Path.Combine(directory.Path, ObjectRuleShardStore.LegacyFileName),
            JsonSerializer.Serialize(new ObjectPriorityRuleManifest { Rules = [Rule(null)] }));
        var service = new ObjectPriorityRuleService(DispatchProxy.Create<IPluginLog, AllianceRuleScopeTests.NoOpProxy>(),
            null!, directory.Path, [context.CurrentDuty!], null, null, null);
        var manifest = new ObjectPriorityRuleManifest { Rules = [Rule(mode)] };
        var payload = JsonSerializer.Serialize(manifest);
        Assert.Equal(expected, CrystalTowerStrategy.Parse(mode));
        if (expected == CrystalTowerStrategyMode.Invalid)
        {
            Assert.False(service.TryImportManifestText(payload, out _, out var reason));
            Assert.Contains("strategy", reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(service.SaveManifest("Personal renamed preset", manifest));
            return;
        }
        Assert.True(service.TryImportManifestText(payload, out var imported, out _));
        Assert.Equal(mode, Assert.Single(imported.Rules).CrystalTowerStrategy);
        Assert.True(service.SaveManifest("Personal renamed preset", imported), service.LastLoadStatus);
        Assert.Equal(expected, CrystalTowerStrategy.Resolve(service.Current.Rules, context));
        var editable = service.CreateEditableCopy();
        var editor = ObjectRuleEditorWindow.CloneManifest(editable);
        var partialImport = PartialRuleImportPlanner.CloneRule(Assert.Single(editor.Rules));
        Assert.Equal(mode, partialImport.CrystalTowerStrategy);
        Assert.NotSame(service.Current.Rules.Single(), editable.Rules.Single());
        Assert.True(service.Reload(), service.LastLoadStatus);
        Assert.Equal("Personal renamed preset", service.ActivePresetName);
        Assert.Equal(mode, Assert.Single(service.Current.Rules).CrystalTowerStrategy);
    }

    [Fact]
    public void ConflictingModesAndWrongDutyCannotSilentlyChooseAStrategy()
    {
        var context = Context();
        Assert.Equal(CrystalTowerStrategyMode.Invalid,
            CrystalTowerStrategy.Resolve([Rule("SYNC"), Rule("UNSYNC")], context));
        Assert.Equal(CrystalTowerStrategyMode.Invalid,
            CrystalTowerStrategy.Resolve([Rule("bad")], context));
        var disabled = Rule("SYNC"); disabled.Enabled = false;
        Assert.Equal(CrystalTowerStrategyMode.None, CrystalTowerStrategy.Resolve([disabled], context));
        Assert.Equal(CrystalTowerStrategyMode.None, CrystalTowerStrategy.Resolve([Rule("SYNC")],
            TestDutyContextFactory.Create(territoryId: 372, cfcId: 102)));
        Assert.Equal(CrystalTowerStrategyMode.None, CrystalTowerStrategy.Resolve([Rule("SYNC")],
            TestDutyContextFactory.Create()));
    }

    [Theory]
    [InlineData(174u, 92u, true)]
    [InlineData(372u, 102u, true)]
    [InlineData(151u, 111u, true)]
    [InlineData(174u, 102u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(777u, 888u, false)]
    public void StrategyIsRestrictedToExactReviewedDutyPairs(uint territory, uint cfc, bool valid)
    {
        var rule = Rule("SYNC"); rule.TerritoryTypeId = territory; rule.ContentFinderConditionId = cfc;
        Assert.Equal(valid, CrystalTowerStrategy.IsValidRule(rule));
        rule.CrystalTowerStrategy = null;
        Assert.True(CrystalTowerStrategy.IsValidRule(rule)); // Existing rules keep their original scope semantics.
    }

    [Fact]
    public void LegacyRuleWithoutStrategyRemainsOptedOutAndExportsWithoutANewField()
    {
        var legacy = JsonSerializer.Deserialize<ObjectPriorityRule>("""{"TerritoryTypeId":174,"ContentFinderConditionId":92}""")!;
        Assert.Null(legacy.CrystalTowerStrategy);
        Assert.Equal(CrystalTowerStrategyMode.None, CrystalTowerStrategy.Resolve([legacy], Context()));
        Assert.DoesNotContain("CrystalTowerStrategy", JsonSerializer.Serialize(legacy));
    }

    private static DutyContextSnapshot Context()
        => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 174, cfcId: 92, alliance: "A");

    private static ObjectPriorityRule Rule(string? mode)
        => new() { TerritoryTypeId = 174, ContentFinderConditionId = 92, ObjectKind = "BattleNpc",
            BaseId = CrystalTowerMechanicPolicy.AtomosBossDataId, Classification = "Required", CrystalTowerStrategy = mode };
}
