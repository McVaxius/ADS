using System.Reflection;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class ObjectRuleShardStoreTests
{
    private static readonly DutyCatalogEntry[] Catalog =
    [
        RemoteJsonUpdateServiceTests.Duty(2, 1037, "the Tam-Tara Deepcroft"),
        RemoteJsonUpdateServiceTests.Duty(1, 1039, "the Thousand Maws of Toto-Rak"),
    ];

    [Fact]
    public void MissingContextInheritsDefaultAndWholeContextOverrideReplacesIt()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default-a"), Rule(1037, "default-b"), Rule(1039, "inherited"));
        var service = CreateService(directory.Path);

        var custom = service.CreateEditableCopy();
        custom.Rules.RemoveAll(rule => rule.TerritoryTypeId == 1037);
        custom.Rules.Insert(0, Rule(1037, "override"));
        Assert.True(service.SaveManifest("Custom", custom));

        Assert.Equal(["override", "inherited"], service.Current.Rules.Select(rule => rule.ObjectName));
        Assert.True(service.HasContextOverride("Custom", "1037_rule_objects.json"));
        Assert.False(service.HasContextOverride("Custom", "1039_rule_objects.json"));
    }

    [Fact]
    public void EmptyOverrideSuppressesInheritedContextAndCustomOnlyTerritoryExecutes()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default"));
        var service = CreateService(directory.Path);
        var baseline = service.CreateEditableCopy();

        var custom = ObjectRuleEditorWindow.CloneManifest(baseline);
        custom.Rules.Clear();
        custom.Rules.Add(Rule(9000, "custom-only"));
        Assert.True(service.SaveManifest("Sparse", custom));
        Assert.True(service.ActivatePreset("Sparse", notify: false));

        Assert.Equal(["custom-only"], service.Current.Rules.Select(rule => rule.ObjectName));
        Assert.True(service.HasContextOverride("Sparse", "1037_rule_objects.json"));
        Assert.Empty(JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(
            File.ReadAllText(service.GetContextShardPath("Sparse", "1037_rule_objects.json")))!.Rules);
        Assert.True(service.HasContextOverride("Sparse", "9000_rule_objects.json"));
    }

    [Fact]
    public void ActivePresetSelectionPersistsAndMissingPresetFallsBack()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default"));
        var configuration = new Configuration { ActiveObjectRulePreset = "DEFAULT" };
        var saves = 0;
        var service = CreateService(directory.Path, configuration, () => saves++);
        var custom = service.CreateEditableCopy();
        custom.Rules[0].ObjectName = "active";
        Assert.True(service.SaveManifest("Remembered", custom));
        Assert.Equal("Remembered", configuration.ActiveObjectRulePreset);

        var reloaded = CreateService(directory.Path, configuration, () => saves++);
        Assert.Equal("Remembered", reloaded.ActivePresetName);
        Assert.Equal("active", reloaded.Current.Rules.Single().ObjectName);

        Directory.Delete(reloaded.GetPresetPath("Remembered"), recursive: true);
        var fallbackToasts = new List<string>();
        var fallback = CreateService(directory.Path, configuration, () => saves++, fallbackToasts.Add);
        Assert.Equal(ObjectPriorityRuleService.DefaultPresetName, fallback.ActivePresetName);
        Assert.Equal(ObjectPriorityRuleService.DefaultPresetName, configuration.ActiveObjectRulePreset);
        Assert.Equal("default", fallback.Current.Rules.Single().ObjectName);
        Assert.Equal(["Object rules active preset: DEFAULT (Remembered was unavailable)"], fallbackToasts);
        Assert.True(saves > 0);
    }

    [Fact]
    public void LegacyMigrationCreatesSparsePresetAndLeavesLegacyFilesUntouched()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "same"), Rule(1039, "inherited"));
        var legacyPresetDirectory = Path.Combine(directory.Path, ObjectRuleShardStore.LegacyPresetDirectoryName);
        Directory.CreateDirectory(legacyPresetDirectory);
        var legacyPresetPath = Path.Combine(legacyPresetDirectory, "Migrated.json");
        File.WriteAllText(legacyPresetPath, JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Rules = [Rule(1037, "different")],
        }));
        var legacyMaturePath = Path.Combine(legacyPresetDirectory, "MATURE-PROPOSALS.json");
        File.WriteAllText(legacyMaturePath, "not migrated");

        var service = CreateService(directory.Path);

        Assert.True(File.Exists(Path.Combine(directory.Path, ObjectRuleShardStore.LegacyFileName)));
        Assert.True(File.Exists(legacyPresetPath));
        Assert.True(File.Exists(legacyMaturePath));
        Assert.True(File.Exists(service.GetContextShardPath("Migrated", "1037_rule_objects.json")));
        Assert.False(File.Exists(service.GetContextShardPath("Migrated", "1039_rule_objects.json")));
        Assert.DoesNotContain("MATURE-PROPOSALS", service.GetPresetNames());
        Assert.True(service.ActivatePreset("Migrated", notify: false));
        Assert.Equal(["different", "inherited"], service.Current.Rules.Select(rule => rule.ObjectName));
    }

    [Fact]
    public void DefaultSaveRequiresExplicitDebugAuthorityInChangedContextApi()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "before"));
        var service = CreateService(directory.Path);
        var baseline = service.CreateEditableCopy();
        var changed = ObjectRuleEditorWindow.CloneManifest(baseline);
        changed.Rules[0].ObjectName = "after";

        Assert.False(service.SaveChangedContexts("DEFAULT", baseline, changed, false, out _, out _));
        Assert.Equal("before", service.Current.Rules.Single().ObjectName);
        Assert.True(service.SaveChangedContexts("DEFAULT", baseline, changed, true, out _, out _));
        Assert.Equal("after", service.Current.Rules.Single().ObjectName);
        Assert.False(service.SaveChangedContexts("DEFAULT", changed, baseline, false, out _, out _));
        Assert.Equal("after", service.Current.Rules.Single().ObjectName);
    }

    private static ObjectPriorityRuleService CreateService(
        string path,
        Configuration? configuration = null,
        Action? save = null,
        Action<string>? showToast = null)
    {
        var log = DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
        return new ObjectPriorityRuleService(log, null!, path, Catalog, configuration, save, showToast);
    }

    private static void WriteLegacyDefault(string path, params ObjectPriorityRule[] rules)
        => File.WriteAllText(Path.Combine(path, ObjectRuleShardStore.LegacyFileName), JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Description = "legacy",
            Rules = [.. rules],
        }));

    private static ObjectPriorityRule Rule(uint territory, string name)
    {
        var duty = Catalog.FirstOrDefault(entry => entry.TerritoryTypeId == territory);
        return new ObjectPriorityRule
        {
            TerritoryTypeId = territory,
            ContentFinderConditionId = duty?.ContentFinderConditionId ?? 0,
            DutyEnglishName = duty?.EnglishName ?? string.Empty,
            ObjectName = name,
        };
    }
}
