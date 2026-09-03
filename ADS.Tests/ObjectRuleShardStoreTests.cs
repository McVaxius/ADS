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

    [Fact]
    public void ContextDescriptorsIncludeCatalogAndCurrentTerritoriesWithBackingState()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default"));
        var service = CreateService(directory.Path);
        var baseline = service.CreateEditableCopy();
        var draft = ObjectRuleEditorWindow.CloneManifest(baseline);
        draft.Rules.Add(Rule(7777, "draft-only"));

        var descriptors = service.GetContextDescriptors(draft, baseline, 7777)
            .ToDictionary(descriptor => descriptor.FileName, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ObjectRuleContextBackingState.NoFileYet, descriptors[ObjectRuleShardStore.GlobalFileName].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.DefaultFile, descriptors["1037_rule_objects.json"].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.NoFileYet, descriptors["1039_rule_objects.json"].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.NoFileYet, descriptors["7777_rule_objects.json"].BackingState);
        Assert.True(descriptors["7777_rule_objects.json"].HasUnsavedChanges);
        Assert.Equal(1, descriptors["7777_rule_objects.json"].EffectiveRowCount);
        Assert.Equal("the Tam-Tara Deepcroft", descriptors["1037_rule_objects.json"].Name);
    }

    [Fact]
    public void CustomContextDescriptorsReportInheritedOverrideEmptyAndCustomOnlyStates()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(0, "global"), Rule(1037, "default-a"), Rule(1039, "default-b"));
        var service = CreateService(directory.Path);
        var custom = service.CreateEditableCopy();
        custom.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "override";
        custom.Rules.RemoveAll(rule => rule.TerritoryTypeId == 1039);
        custom.Rules.Add(Rule(9000, "custom-only"));
        Assert.True(service.SaveManifest("Custom", custom));

        var descriptors = service.GetContextDescriptors(service.CreateEditableCopy(), service.CreateEditableCopy(), 0)
            .ToDictionary(descriptor => descriptor.FileName);

        Assert.Equal(ObjectRuleContextBackingState.InheritedDefault, descriptors[ObjectRuleShardStore.GlobalFileName].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.OverrideFile, descriptors["1037_rule_objects.json"].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.EmptyOverride, descriptors["1039_rule_objects.json"].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.CustomOnlyFile, descriptors["9000_rule_objects.json"].BackingState);
    }

    [Fact]
    public void Schema24ContextSelectionPersistsAndInvalidValuesFallBackSafely()
    {
        var configuration = new Configuration
        {
            ObjectRuleSelectedContextFileNames = ["1037_rule_objects.json", "bad.json", ObjectRuleShardStore.GlobalFileName],
            ObjectRuleEditorCompactMode = true,
        };
        var roundTrip = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(configuration))!;

        Assert.Equal(24, roundTrip.Version);
        Assert.True(roundTrip.ObjectRuleEditorCompactMode);
        Assert.Equal(
            [ObjectRuleShardStore.GlobalFileName, "1037_rule_objects.json"],
            ObjectRuleEditorWindow.NormalizeSelectedContextFileNames(roundTrip.ObjectRuleSelectedContextFileNames));
        Assert.Empty(ObjectRuleEditorWindow.NormalizeSelectedContextFileNames(["bad.json", "1037_RULE_OBJECTS.json"]));
    }

    [Fact]
    public void PromotionContextResolutionUsesAllSavedOverridesOnlyWhenNoContextsAreChecked()
    {
        var descriptors = new[]
        {
            Descriptor(ObjectRuleShardStore.GlobalFileName, hasDefaultFile: true),
            Descriptor("1037_rule_objects.json", hasDefaultFile: true, hasCustomOverride: true),
            Descriptor("1039_rule_objects.json", hasDefaultFile: true, hasCustomOverride: true, isEmptyOverride: true),
            Descriptor("9000_rule_objects.json", hasCustomOverride: true, isCustomOnly: true),
            Descriptor("9999_rule_objects.json"),
        };

        var all = ObjectRuleEditorWindow.ResolvePromotionContextDescriptors(
            descriptors,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            ["1037_rule_objects.json", "1039_rule_objects.json", "9000_rule_objects.json"],
            all.Select(descriptor => descriptor.FileName));
        Assert.Contains(all, descriptor => descriptor.IsEmptyOverride);
        Assert.Contains(all, descriptor => descriptor.IsCustomOnly);
        Assert.DoesNotContain(all, descriptor => descriptor.BackingState is ObjectRuleContextBackingState.InheritedDefault or ObjectRuleContextBackingState.NoFileYet);

        var explicitlySelected = ObjectRuleEditorWindow.ResolvePromotionContextDescriptors(
            descriptors,
            new HashSet<string>(
                [ObjectRuleShardStore.GlobalFileName, "1037_rule_objects.json", "9999_rule_objects.json"],
                StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["1037_rule_objects.json"], explicitlySelected.Select(descriptor => descriptor.FileName));
    }

    private static ObjectRuleContextDescriptor Descriptor(
        string fileName,
        bool hasDefaultFile = false,
        bool hasCustomOverride = false,
        bool isEmptyOverride = false,
        bool isCustomOnly = false)
        => new(
            FileName: fileName,
            TerritoryTypeId: fileName == ObjectRuleShardStore.GlobalFileName ? null : (uint?)1,
            Name: fileName,
            IsDefaultPreset: false,
            HasDefaultFile: hasDefaultFile,
            HasCustomOverride: hasCustomOverride,
            EffectiveRowCount: isEmptyOverride ? 0 : 1,
            IsEmptyOverride: isEmptyOverride,
            IsCustomOnly: isCustomOnly,
            HasUnsavedChanges: false);

    [Fact]
    public void ChangedInheritedContextSavesCompleteOverrideAndFirstCustomOnlyRow()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "first"), Rule(1037, "second"), Rule(1039, "inherited"));
        var service = CreateService(directory.Path);
        Assert.True(service.SaveManifest("Custom", service.CreateEditableCopy()));
        var baseline = service.CreateEditableCopy();
        var draft = ObjectRuleEditorWindow.CloneManifest(baseline);
        draft.Rules.First(rule => rule.ObjectName == "first").ObjectName = "edited";
        draft.Rules.Add(Rule(9000, "custom-only"));

        Assert.True(service.SaveChangedContexts("Custom", baseline, draft, false, out var saved, out var status), status);

        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], saved);
        var completeOverride = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(
            File.ReadAllText(service.GetContextShardPath("Custom", "1037_rule_objects.json")))!;
        Assert.Equal(["edited", "second"], completeOverride.Rules.Select(rule => rule.ObjectName));
        var descriptors = service.GetContextDescriptors(service.CreateEditableCopy(), service.CreateEditableCopy(), 0)
            .ToDictionary(descriptor => descriptor.FileName);
        Assert.Equal(ObjectRuleContextBackingState.OverrideFile, descriptors["1037_rule_objects.json"].BackingState);
        Assert.Equal(ObjectRuleContextBackingState.CustomOnlyFile, descriptors["9000_rule_objects.json"].BackingState);
    }

    [Fact]
    public void BatchRevertSkipsInheritedAndRemovesCustomOnlyContext()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default"), Rule(1039, "inherited"));
        var service = CreateService(directory.Path);
        var custom = service.CreateEditableCopy();
        custom.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "override";
        custom.Rules.Add(Rule(9000, "custom-only"));
        Assert.True(service.SaveManifest("Custom", custom));

        Assert.True(service.TryRevertContextsToDefault(
            "Custom",
            ["1037_rule_objects.json", "1039_rule_objects.json", "9000_rule_objects.json"],
            out var deleted,
            out var skipped,
            out var status), status);

        Assert.Equal(["1037_rule_objects.json", "9000_rule_objects.json"], deleted);
        Assert.Equal(["1039_rule_objects.json"], skipped);
        Assert.Equal(["default", "inherited"], service.Current.Rules.Select(rule => rule.ObjectName));
        Assert.DoesNotContain(
            service.GetContextDescriptors(service.CreateEditableCopy(), service.CreateEditableCopy(), 0),
            descriptor => descriptor.FileName == "9000_rule_objects.json");
    }

    [Fact]
    public void SavingDirtyOverrideReloadsUnrelatedRefreshedDefaultContext()
    {
        using var directory = new TempDirectory();
        WriteLegacyDefault(directory.Path, Rule(1037, "default-a"), Rule(1039, "default-b"));
        var service = CreateService(directory.Path);
        var custom = service.CreateEditableCopy();
        custom.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "custom-a";
        Assert.True(service.SaveManifest("Custom", custom));
        var baseline = service.CreateEditableCopy();

        WriteShard(service.GetContextShardPath("DEFAULT", "1039_rule_objects.json"), Rule(1039, "remote-b"));
        var draft = ObjectRuleEditorWindow.CloneManifest(baseline);
        draft.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "custom-a-2";

        Assert.True(service.SaveChangedContexts("Custom", baseline, draft, false, out _, out var status), status);
        Assert.Equal("custom-a-2", service.Current.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName);
        Assert.Equal("remote-b", service.Current.Rules.First(rule => rule.TerritoryTypeId == 1039).ObjectName);
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

    private static void WriteShard(string path, params ObjectPriorityRule[] rules)
        => ObjectRuleShardStore.WriteJsonAtomic(path, JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
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
