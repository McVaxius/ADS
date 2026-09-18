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

    [Fact]
    public void NamedPresetTransferPreservesSparseContextsAndUnrelatedDestinationFiles()
    {
        using var sourceDirectory = new TempDirectory();
        WriteLegacyDefault(sourceDirectory.Path,
            Rule(1037, "default"), Rule(1039, "inherited"), Rule(1040, "suppress"),
            Rule(1041, "equal saved override"), Rule(1042, "draft inherited-a"), Rule(1042, "draft inherited-b"));
        var source = CreateService(sourceDirectory.Path);
        var custom = source.CreateEditableCopy();
        custom.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "saved override";
        custom.Rules.RemoveAll(rule => rule.TerritoryTypeId == 1040);
        custom.Rules.Add(Rule(9000, "saved custom-only"));
        custom.Rules.Add(Rule(9002, "clear in draft"));
        Assert.True(source.SaveManifest("Shared Rules", custom));
        WriteShard(source.GetContextShardPath("Shared Rules", "1041_rule_objects.json"), Rule(1041, "equal saved override"));
        var baseline = source.CreateEditableCopy();
        var draft = ObjectRuleEditorWindow.CloneManifest(baseline);
        draft.Rules.First(rule => rule.TerritoryTypeId == 1037).ObjectName = "draft override";
        draft.Rules.First(rule => rule.TerritoryTypeId == 1042).ObjectName = "changed inherited-a";
        draft.Rules.RemoveAll(rule => rule.TerritoryTypeId == 9002);
        draft.Rules.Add(Rule(9001, "draft custom-only"));

        Assert.False(source.TryExportPresetText("DEFAULT", baseline, draft, out _, out _));
        Assert.True(source.TryExportPresetText("Shared Rules", baseline, draft, out var json, out var status), status);
        Assert.True(source.TryImportRulesText(json, out var transfer, out _, out status), status);
        Assert.NotNull(transfer);
        Assert.Equal("Shared Rules", transfer.PresetName);
        Assert.Equal(1, transfer.TransferVersion);
        Assert.Equal(["1037_rule_objects.json", "1040_rule_objects.json", "1041_rule_objects.json", "1042_rule_objects.json",
            "9000_rule_objects.json", "9001_rule_objects.json", "9002_rule_objects.json"], transfer.Contexts.Keys);
        Assert.DoesNotContain("1039_rule_objects.json", transfer.Contexts.Keys);
        Assert.Empty(transfer.Contexts["1040_rule_objects.json"].Rules);
        Assert.Empty(transfer.Contexts["9002_rule_objects.json"].Rules);
        Assert.Equal("draft override", transfer.Contexts["1037_rule_objects.json"].Rules.Single().ObjectName);
        Assert.Equal(["changed inherited-a", "draft inherited-b"], transfer.Contexts["1042_rule_objects.json"].Rules.Select(rule => rule.ObjectName));
        Assert.Equal("saved custom-only", transfer.Contexts["9000_rule_objects.json"].Rules.Single().ObjectName);
        Assert.Equal("draft custom-only", transfer.Contexts["9001_rule_objects.json"].Rules.Single().ObjectName);

        using var destinationDirectory = new TempDirectory();
        WriteLegacyDefault(destinationDirectory.Path, Rule(1037, "destination default"), Rule(1039, "recipient inherited"), Rule(1040, "recipient suppress"));
        var destination = CreateService(destinationDirectory.Path);
        var transferPath = Path.Combine(destinationDirectory.Path, "shared-preset.json");
        ObjectRuleShardStore.WriteJsonAtomic(transferPath, json);
        Assert.True(destination.TryImportRulesText(File.ReadAllText(transferPath), out transfer, out _, out status), status);
        Assert.NotNull(transfer);
        var state = destination.CaptureContextFileState(transfer.PresetName, transfer.Contexts.Keys);
        Assert.True(destination.ImportAndSavePreset(transfer, state, out status), status);
        Assert.Equal("Shared Rules", destination.ActivePresetName);
        Assert.Contains(destination.Current.Rules, rule => rule.ObjectName == "recipient inherited");
        Assert.DoesNotContain(destination.Current.Rules, rule => rule.TerritoryTypeId == 1040);
        Assert.True(destination.HasContextOverride("Shared Rules", "9002_rule_objects.json"));

        var unrelatedShard = destination.GetContextShardPath("Shared Rules", "1039_rule_objects.json");
        WriteShard(unrelatedShard, Rule(1039, "recipient override"));
        var unrelatedFile = Path.Combine(destination.GetPresetPath("Shared Rules"), "personal.txt");
        File.WriteAllText(unrelatedFile, "keep this file exactly");
        var unrelatedJson = File.ReadAllText(unrelatedShard);
        var defaultJson = File.ReadAllText(destination.GetContextShardPath("DEFAULT", "1037_rule_objects.json"));
        state = destination.CaptureContextFileState(transfer.PresetName, transfer.Contexts.Keys);
        Assert.True(destination.ImportAndSavePreset(transfer, state, out status), status);
        Assert.Equal(unrelatedJson, File.ReadAllText(unrelatedShard));
        Assert.Equal("keep this file exactly", File.ReadAllText(unrelatedFile));
        Assert.Equal(defaultJson, File.ReadAllText(destination.GetContextShardPath("DEFAULT", "1037_rule_objects.json")));
        Assert.Contains(destination.Current.Rules, rule => rule.ObjectName == "recipient override");

        // Reject a bad final context before touching a valid earlier context or creating a new destination.
        var includedPath = destination.GetContextShardPath("Shared Rules", "1037_rule_objects.json");
        var includedJson = File.ReadAllText(includedPath);
        transfer.Contexts["1037_rule_objects.json"].Rules[0].ObjectName = "must not be written";
        transfer.Contexts.Add("9003_rule_objects.json", new ObjectPriorityRuleManifest { Rules = [Rule(9004, "wrong context")] });
        state = destination.CaptureContextFileState(transfer.PresetName, transfer.Contexts.Keys);
        Assert.False(destination.ImportAndSavePreset(transfer, state, out _));
        Assert.Equal(includedJson, File.ReadAllText(includedPath));
        transfer.PresetName = "Invalid New Preset";
        state = destination.CaptureContextFileState(transfer.PresetName, transfer.Contexts.Keys);
        Assert.False(destination.ImportAndSavePreset(transfer, state, out _));
        Assert.False(Directory.Exists(destination.GetPresetPath(transfer.PresetName)));

        Assert.True(destination.TryImportRulesText(json, out transfer, out _, out status), status);
        Assert.NotNull(transfer);
        state = destination.CaptureContextFileState(transfer.PresetName, transfer.Contexts.Keys);
        WriteShard(includedPath, Rule(1037, "external change after preview"));
        Assert.False(destination.ImportAndSavePreset(transfer, state, out status));
        Assert.Contains("conflict", status);
        Assert.Contains("external change after preview", File.ReadAllText(includedPath));

        Assert.False(destination.TryImportRulesText(json.Replace("\"TransferVersion\": 1", "\"TransferVersion\": 2"), out _, out _, out _));
        Assert.False(destination.TryImportRulesText(json.Replace("Shared Rules", "DEFAULT"), out _, out _, out _));
        Assert.False(destination.TryImportRulesText(json.Replace("Shared Rules", "../outside"), out _, out _, out _));
        Assert.False(destination.TryImportRulesText(json.Replace("1037_rule_objects.json", "../1037_rule_objects.json"), out _, out _, out _));
        Assert.False(destination.TryImportRulesText("{\"TransferVersion\":1,\"PresetName\":\"Bad\",\"Contexts\":{\"9000_rule_objects.json\":{\"SchemaVersion\":1}}}", out _, out _, out _));
        Assert.True(destination.TryImportRulesText(JsonSerializer.Serialize(draft), out var legacyTransfer, out var legacyManifest, out status), status);
        Assert.Null(legacyTransfer);
        Assert.Equal(draft.Rules.Count, legacyManifest.Rules.Count);
        Assert.True(destination.TryImportRulesText("// legacy manifest\n" + JsonSerializer.Serialize(draft), out legacyTransfer, out legacyManifest, out status), status);
        Assert.Null(legacyTransfer);
        Assert.Equal(draft.Rules.Count, legacyManifest.Rules.Count);
        Assert.True(destination.TryImportRulesText(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)), out transfer, out _, out status), status);
        Assert.Equal("Shared Rules", transfer!.PresetName);
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
