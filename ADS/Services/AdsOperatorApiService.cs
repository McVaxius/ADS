using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

public sealed class AdsOperatorApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Plugin plugin;

    public AdsOperatorApiService(Plugin plugin)
        => this.plugin = plugin;

    public string GetCapabilitiesJson()
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            settings = AdsIpcValidation.KnownConfigurationSettings.OrderBy(x => x).ToArray(),
            preferredSettings = new[] { "desynthInventoryScope" },
            deprecatedSettings = new[] { "desynthCategories", "desynthProtectGearsets" },
            desynthInventoryScopes = Enum.GetNames<DesynthInventoryScope>(),
            desynthRunModes = Enum.GetValues<DesynthRunMode>().Select(DesynthPolicyService.GetModeName).ToArray(),
            actions = new[]
            {
                "duty.start-outside", "duty.start-inside", "duty.resume-inside", "duty.leave",
                "window.open-loot", "window.toggle-loot", "window.open-desynth",
                "utility.start-repair", "utility.start-desynth", "utility.cancel",
                "preset.create", "preset.rename", "preset.delete", "preset.select", "preset.add-item",
                "preset.remove-item", "preset.import-raw", "preset.import-base64", "preset.export-raw",
                "preset.export-base64", "ledger.clear", "configuration.patch",
            },
        });

    public string GetConfigurationJson()
        => JsonSerializer.Serialize(new
        {
            plugin.Configuration.PluginEnabled,
            plugin.Configuration.LootMode,
            plugin.Configuration.LootRegistrableNeedingEnabled,
            plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty,
            plugin.Configuration.HigherLowerAutomationEnabled,
            plugin.Configuration.DesynthSource,
            plugin.Configuration.DesynthInventoryScope,
            plugin.Configuration.DesynthActivePreset,
            plugin.Configuration.DesynthSkillUpFilterEnabled,
            plugin.Configuration.DesynthSkillUpThreshold,
            plugin.Configuration.DesynthContextMenuEnabled,
        });

    public string Invoke(string action, string payloadJson)
    {
        if (!AdsIpcValidation.IsKnownAction(action))
            return Result(action, false, $"Unknown action '{action}'.");

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            var payload = document.RootElement;
            return action.Trim().ToLowerInvariant() switch
            {
                "duty.start-outside" => Result(action, plugin.StartDutyFromOutside()),
                "duty.start-inside" => Result(action, plugin.StartDutyFromInside()),
                "duty.resume-inside" => Result(action, plugin.ResumeDutyFromInside()),
                "duty.leave" => Result(action, plugin.LeaveDuty()),
                "window.open-loot" => Result(action, Open(plugin.OpenLootUi)),
                "window.toggle-loot" => Result(action, Open(plugin.ToggleLootUi)),
                "window.open-desynth" => Result(action, Open(plugin.OpenDesynthConfigUi)),
                "utility.start-repair" => Result(action, plugin.StartRepair(GetString(payload, "mode"))),
                "utility.start-desynth" => Result(action, plugin.StartDesynth(GetString(payload, "mode"))),
                "utility.cancel" => Result(action, plugin.CancelUtility()),
                "preset.create" => PresetResult(action, plugin.DesynthPresetStore.Create(GetString(payload, "name"), GetString(payload, "description"), out var createError), createError),
                "preset.rename" => PresetResult(action, plugin.RenameDesynthPreset(GetString(payload, "name"), GetString(payload, "newName"), out var renameError), renameError),
                "preset.delete" => PresetResult(action, plugin.DeleteDesynthPreset(GetString(payload, "name"), out var deleteError), deleteError),
                "preset.select" => PresetResult(action, plugin.SelectDesynthPreset(GetString(payload, "name"), out var selectError), selectError),
                "preset.add-item" => PresetResult(action, MutatePresetItem(payload, true, out var addError), addError),
                "preset.remove-item" => PresetResult(action, MutatePresetItem(payload, false, out var removeError), removeError),
                "preset.import-raw" => PresetResult(action, plugin.ImportDesynthPresetsRaw(GetString(payload, "json"), out var rawError), rawError),
                "preset.import-base64" => PresetResult(action, plugin.ImportDesynthPresetsBase64(GetString(payload, "base64"), out var base64Error), base64Error),
                "preset.export-raw" => Result(action, true, data: plugin.DesynthPresetStore.ExportRaw()),
                "preset.export-base64" => Result(action, true, data: plugin.DesynthPresetStore.ExportBase64()),
                "ledger.clear" => Result(action, ClearLedger()),
                "configuration.patch" => PatchConfigurationJson(payload.GetRawText(), action),
                _ => Result(action, false, $"Unknown action '{action}'."),
            };
        }
        catch (Exception ex)
        {
            return Result(action, false, $"Invalid payload: {ex.Message}");
        }
    }

    public string PatchConfigurationJson(string patchJson)
        => PatchConfigurationJson(patchJson, "configuration.patch");

    private string PatchConfigurationJson(string patchJson, string action)
    {
        try
        {
            using var document = JsonDocument.Parse(patchJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result(action, false, "Configuration patch must be a JSON object.");

            var changes = new List<Action>();
            var nextScope = DesynthPolicyService.NormalizeScope(plugin.Configuration.DesynthInventoryScope);
            var explicitScopeSeen = false;
            var legacyCategoriesSeen = false;
            var legacyProtectSeen = false;
            List<string>? legacyCategories = null;
            var legacyProtectGearsets = plugin.Configuration.DesynthProtectGearsets;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!AdsIpcValidation.IsKnownConfigurationSetting(property.Name))
                    return Result(action, false, $"Unknown setting '{property.Name}'.");

                switch (property.Name.ToLowerInvariant())
                {
                    case "pluginenabled":
                        var pluginEnabled = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.PluginEnabled = pluginEnabled);
                        break;
                    case "lootregistrableneedingenabled":
                        var lootRegistrable = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.LootRegistrableNeedingEnabled = lootRegistrable);
                        break;
                    case "lootmode":
                        if (!Enum.TryParse<LootRollMode>(property.Value.GetString(), true, out var lootMode))
                            return Result(action, false, "Invalid lootMode.");
                        changes.Add(() => plugin.Configuration.LootMode = lootMode);
                        break;
                    case "processdialogrulesoutsideownedduty":
                        var dialogRules = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.ProcessDialogRulesOutsideOwnedDuty = dialogRules);
                        break;
                    case "higherlowerautomationenabled":
                        var higherLower = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.HigherLowerAutomationEnabled = higherLower);
                        break;
                    case "desynthsource":
                        if (!Enum.TryParse<DesynthSource>(property.Value.GetString(), true, out var source))
                            return Result(action, false, "Invalid desynthSource.");
                        changes.Add(() => plugin.Configuration.DesynthSource = source);
                        break;
                    case "desynthinventoryscope":
                        if (!DesynthPolicyService.TryParseScope(property.Value.GetString(), out var scope))
                            return Result(action, false, "Invalid desynthInventoryScope.");
                        nextScope = scope;
                        explicitScopeSeen = true;
                        break;
                    case "desynthactivepreset":
                        var presetName = property.Value.GetString() ?? string.Empty;
                        if (!plugin.DesynthPresetStore.Presets.Any(x => string.Equals(x.Name, presetName, StringComparison.OrdinalIgnoreCase)))
                            return Result(action, false, $"Unknown preset '{presetName}'.");
                        changes.Add(() => plugin.Configuration.DesynthActivePreset = plugin.DesynthPresetStore.Get(presetName).Name);
                        break;
                    case "desynthskillupfilterenabled":
                        var filter = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.DesynthSkillUpFilterEnabled = filter);
                        break;
                    case "desynthskillupthreshold":
                        var threshold = property.Value.GetInt32();
                        if (threshold is < 0 or > 1000)
                            return Result(action, false, "desynthSkillUpThreshold must be between 0 and 1000.");
                        changes.Add(() => plugin.Configuration.DesynthSkillUpThreshold = threshold);
                        break;
                    case "desynthprotectgearsets":
                        legacyProtectGearsets = property.Value.GetBoolean();
                        legacyProtectSeen = true;
                        break;
                    case "desynthcategories":
                        legacyCategories = property.Value.EnumerateArray()
                            .Select(x => x.GetString() ?? string.Empty)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (!DesynthPolicyService.TryNormalizeLegacyCategories(legacyCategories, legacyProtectGearsets, out _, out var categoryError))
                            return Result(action, false, categoryError);
                        legacyCategoriesSeen = true;
                        break;
                    case "desynthcontextmenuenabled":
                        var contextMenu = property.Value.GetBoolean();
                        changes.Add(() => plugin.Configuration.DesynthContextMenuEnabled = contextMenu);
                        break;
                    default:
                        return Result(action, false, $"Unknown setting '{property.Name}'.");
                }
            }

            if (!explicitScopeSeen && (legacyCategoriesSeen || legacyProtectSeen))
            {
                var categories = legacyCategoriesSeen
                    ? legacyCategories
                    : DesynthPolicyService.GetCategoryNames(nextScope);
                var protect = legacyProtectSeen
                    ? legacyProtectGearsets
                    : DesynthPolicyService.ScopeProtectsGearsets(nextScope);
                if (!DesynthPolicyService.TryNormalizeLegacyCategories(categories, protect, out nextScope, out var categoryError))
                    return Result(action, false, categoryError);
            }

            if (explicitScopeSeen || legacyCategoriesSeen || legacyProtectSeen)
                changes.Add(() => DesynthPolicyService.ApplyScopeToConfiguration(plugin.Configuration, nextScope));

            foreach (var change in changes)
                change();
            plugin.SaveConfiguration();
            return Result(action, true, "Configuration patched.", plugin.GetConfigurationJson());
        }
        catch (Exception ex)
        {
            return Result(action, false, $"Invalid configuration patch: {ex.Message}");
        }
    }

    private bool ClearLedger()
    {
        plugin.DesynthDutyLedgerStore.Clear();
        return true;
    }

    private bool MutatePresetItem(JsonElement payload, bool add, out string error)
    {
        var presetName = GetString(payload, "preset");
        if (string.IsNullOrWhiteSpace(presetName))
            presetName = plugin.Configuration.DesynthActivePreset;
        var itemId = GetUInt(payload, "itemId");
        var value = itemId > 0
            ? itemId.ToString()
            : GetString(payload, "itemName");
        if (!plugin.TryResolveDesynthItemId(value, out itemId))
        {
            error = $"Could not resolve item '{value}' by ID or exact name.";
            return false;
        }

        return add
            ? plugin.DesynthPresetStore.AddItem(presetName, itemId, out error)
            : plugin.DesynthPresetStore.RemoveItem(presetName, itemId, out error);
    }

    private static bool Open(Action action)
    {
        action();
        return true;
    }

    private static string PresetResult(string action, bool success, string error)
        => Result(action, success, success ? "Preset operation completed." : error);

    private static string Result(string action, bool success, string? message = null, object? data = null)
        => JsonSerializer.Serialize(new { success, action, message = message ?? (success ? "Accepted." : "Rejected."), data });

    private static string GetString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static uint GetUInt(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result) ? result : 0;
}
