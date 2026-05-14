using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class TreasureDungeonRoleDetector
{
    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly string configDirectory;

    public TreasureDungeonRoleDetector(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        IPluginLog log,
        string configDirectory)
    {
        this.pluginInterface = pluginInterface;
        this.objectTable = objectTable;
        this.log = log;
        this.configDirectory = configDirectory;
    }

    public TreasureDungeonRoleInference Infer()
    {
        var characterKey = ResolveCurrentCharacterKey();
        var frenRiderLoaded = IsPluginLoaded("FrenRider", "FrenRider");
        var frenRider = frenRiderLoaded
            ? ReadFrenRiderEnabled(characterKey)
            : new ConfigBoolRead(false, "FrenRider is not loaded.");

        if (frenRiderLoaded && frenRider.Enabled)
        {
            return new TreasureDungeonRoleInference(
                TreasureDungeonRole.Follower,
                "FrenRider",
                frenRider.Detail,
                characterKey,
                FrenRiderLoaded: true,
                FrenRiderEnabled: true,
                LootGoblinLoaded: IsPluginLoaded("LootGoblin", "LootGoblin"),
                LootGoblinEnabled: false,
                LootGoblinAdsSolverEnabled: false);
        }

        var lootGoblinLoaded = IsPluginLoaded("LootGoblin", "LootGoblin");
        var lootGoblin = lootGoblinLoaded
            ? ReadLootGoblinEnabled()
            : new LootGoblinConfigRead(false, false, "LootGoblin is not loaded.");

        if (lootGoblinLoaded && lootGoblin.Enabled && lootGoblin.UseAdsInsteadOfLegacyDungeonSolver)
        {
            return new TreasureDungeonRoleInference(
                TreasureDungeonRole.MapOpener,
                "LootGoblin",
                lootGoblin.Detail,
                characterKey,
                FrenRiderLoaded: frenRiderLoaded,
                FrenRiderEnabled: frenRider.Enabled,
                LootGoblinLoaded: true,
                LootGoblinEnabled: true,
                LootGoblinAdsSolverEnabled: true);
        }

        var detail = $"Default map-opener behavior. FrenRider: {frenRider.Detail} LootGoblin: {lootGoblin.Detail}";
        return new TreasureDungeonRoleInference(
            TreasureDungeonRole.MapOpener,
            "Default",
            detail,
            characterKey,
            FrenRiderLoaded: frenRiderLoaded,
            FrenRiderEnabled: frenRider.Enabled,
            LootGoblinLoaded: lootGoblinLoaded,
            LootGoblinEnabled: lootGoblin.Enabled,
            LootGoblinAdsSolverEnabled: lootGoblin.UseAdsInsteadOfLegacyDungeonSolver);
    }

    private ConfigBoolRead ReadFrenRiderEnabled(string characterKey)
    {
        var files = EnumerateCandidateFiles("FrenRider", "*_FrenRider.json");
        if (files.Count == 0)
            return new ConfigBoolRead(false, "FrenRider is loaded, but no *_FrenRider.json account config files were found.");

        if (string.IsNullOrWhiteSpace(characterKey))
            return new ConfigBoolRead(false, $"FrenRider is loaded, but ADS could not resolve the current character key before reading {files.Count} config file(s).");

        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), JsonDocumentOptions);
                var root = document.RootElement;
                if (!TryGetProperty(root, "Characters", out var characters) || characters.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var character in characters.EnumerateObject())
                {
                    if (!string.Equals(character.Name, characterKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryGetBool(character.Value, "Enabled", out var enabled))
                    {
                        var detail = $"FrenRider is loaded and {character.Name} in {Path.GetFileName(file)} has Enabled={enabled}.";
                        return new ConfigBoolRead(enabled, detail);
                    }

                    return new ConfigBoolRead(false, $"FrenRider is loaded and {character.Name} exists in {Path.GetFileName(file)}, but Enabled was missing or unreadable.");
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[ADS] Failed to read FrenRider config {file}: {ex.Message}");
            }
        }

        return new ConfigBoolRead(false, $"FrenRider is loaded, but no current-character entry matched {characterKey} in {files.Count} *_FrenRider.json file(s).");
    }

    private LootGoblinConfigRead ReadLootGoblinEnabled()
    {
        var files = EnumerateCandidateFiles("LootGoblin", "LootGoblin.json");
        if (files.Count == 0)
            files = EnumerateCandidateFiles("LootGoblin", "*LootGoblin*.json");

        if (files.Count == 0)
            return new LootGoblinConfigRead(false, false, "LootGoblin is loaded, but no LootGoblin.json config file was found.");

        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), JsonDocumentOptions);
                var root = document.RootElement;
                if (!TryGetBool(root, "Enabled", out var enabled)
                    || !TryGetBool(root, "UseAdsInsteadOfLegacyDungeonSolver", out var useAds))
                {
                    continue;
                }

                return new LootGoblinConfigRead(
                    enabled,
                    useAds,
                    $"LootGoblin is loaded and {Path.GetFileName(file)} has Enabled={enabled}, UseAdsInsteadOfLegacyDungeonSolver={useAds}.");
            }
            catch (Exception ex)
            {
                log.Debug($"[ADS] Failed to read LootGoblin config {file}: {ex.Message}");
            }
        }

        return new LootGoblinConfigRead(false, false, $"LootGoblin is loaded, but no readable config with Enabled and UseAdsInsteadOfLegacyDungeonSolver was found across {files.Count} file(s).");
    }

    private bool IsPluginLoaded(string internalName, string nameFragment)
    {
        try
        {
            foreach (var plugin in pluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded)
                    continue;

                if (string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrWhiteSpace(nameFragment)
                    && (plugin.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)
                        || plugin.InternalName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[ADS] Failed to inspect installed plugins while inferring treasure role: {ex.Message}");
        }

        return false;
    }

    private IReadOnlyList<string> EnumerateCandidateFiles(string pluginName, string searchPattern)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateCandidateRoots(pluginName))
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var file in Directory.EnumerateFiles(root, searchPattern, SearchOption.TopDirectoryOnly))
                    files.Add(file);
            }
            catch (Exception ex)
            {
                log.Debug($"[ADS] Failed to enumerate {pluginName} config root {root}: {ex.Message}");
            }
        }

        return files
            .OrderByDescending(GetLastWriteTimeUtcSafe)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> EnumerateCandidateRoots(string pluginName)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(configDirectory);
        AddRoot(Path.Combine(configDirectory, pluginName));

        var parent = Directory.GetParent(configDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            AddRoot(parent);
            AddRoot(Path.Combine(parent, pluginName));
        }

        var grandParent = parent is null ? null : Directory.GetParent(parent)?.FullName;
        if (!string.IsNullOrWhiteSpace(grandParent))
            AddRoot(Path.Combine(grandParent, pluginName));

        return roots;

        void AddRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root))
                roots.Add(root);
        }
    }

    private string ResolveCurrentCharacterKey()
    {
        try
        {
            var player = objectTable.LocalPlayer;
            var characterName = player?.Name.TextValue.Trim() ?? string.Empty;
            var worldName = player?.HomeWorld.Value.Name.ToString().Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(worldName)
                ? string.Empty
                : $"{characterName}@{worldName}";
        }
        catch (Exception ex)
        {
            log.Debug($"[ADS] Failed to resolve current character key while inferring treasure role: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
            return true;

        property = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!TryGetProperty(element, propertyName, out var property))
            return false;

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && bool.TryParse(property.GetString(), out value);
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private readonly record struct ConfigBoolRead(bool Enabled, string Detail);

    private readonly record struct LootGoblinConfigRead(bool Enabled, bool UseAdsInsteadOfLegacyDungeonSolver, string Detail);
}
