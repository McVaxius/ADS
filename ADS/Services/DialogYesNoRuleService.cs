using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DialogYesNoRuleService
{
    public const string DefaultPresetName = "DEFAULT";
    private const string FileName = "dialog-yesno-rules.json";
    private const string PresetDirectoryName = "dialog-rule-presets";
    private static readonly TimeSpan ReloadPollInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly IPluginLog log;
    private readonly string configPath;
    private readonly string presetDirectoryPath;
    private DateTime lastObservedRulesWriteUtc;
    private DateTime nextReloadPollUtc;

    public DialogYesNoRuleService(IPluginLog log, string configDirectory)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        configPath = Path.Combine(configDirectory, FileName);
        presetDirectoryPath = Path.Combine(configDirectory, PresetDirectoryName);
        Directory.CreateDirectory(presetDirectoryPath);
        LastSyncStatus = "DEFAULT dialog rules load from the plugin config cache; the remote updater refreshes this file from botologyupdates.";

        EnsureSeeded();
        Reload();
    }

    public string ConfigPath
        => configPath;

    public string PresetDirectoryPath
        => presetDirectoryPath;

    public string LastLoadStatus { get; private set; } = "Dialog rules not loaded yet.";

    public string LastSyncStatus { get; private set; }

    public DialogYesNoRuleManifest Current { get; private set; } = new();

    public int ActiveRuleCount
        => Current.Rules.Count(x => x.Enabled);

    public DialogYesNoRuleManifest CreateEditableCopy()
        => new()
        {
            SchemaVersion = Current.SchemaVersion,
            Description = Current.Description,
            Rules = Current.Rules.Select(CloneRule).ToList(),
        };

    public DialogYesNoRule CreateBlankRule()
        => new();

    public IEnumerable<DialogYesNoRule> GetEnabledRules()
        => Current.Rules.Where(x => x.Enabled);

    public bool IsDefaultPreset(string presetName)
        => string.Equals(presetName, DefaultPresetName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetPresetNames()
    {
        var names = new List<string> { DefaultPresetName };
        if (!Directory.Exists(presetDirectoryPath))
            return names;

        names.AddRange(
            Directory.EnumerateFiles(presetDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !string.Equals(x, DefaultPresetName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)!);
        return names;
    }

    public string GetPresetPath(string presetName)
        => IsDefaultPreset(presetName)
            ? configPath
            : Path.Combine(presetDirectoryPath, $"{SanitizePresetName(presetName)}.json");

    public string SanitizePresetName(string presetName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleaned = new string((presetName ?? string.Empty).Where(ch => !invalidCharacters.Contains(ch)).ToArray());
        cleaned = NormalizeOptional(cleaned).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Preset";

        if (IsDefaultPreset(cleaned))
            cleaned = $"{DefaultPresetName}-copy";

        return cleaned;
    }

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            if (!TryLoadManifestFromPath(configPath, out var manifest, out var status, persistMigrations: true))
            {
                Current = new DialogYesNoRuleManifest();
                LastLoadStatus = status;
                log.Warning($"[ADS] {LastLoadStatus}");
                return false;
            }

            Current = manifest;
            lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
            LastLoadStatus = status;
            log.Information($"[ADS] {LastLoadStatus}");
            return true;
        }
        catch (Exception ex)
        {
            RememberCurrentRulesWriteTime();
            Current = new DialogYesNoRuleManifest();
            LastLoadStatus = $"Failed to load {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool ReloadIfChanged()
    {
        var now = DateTime.UtcNow;
        if (now < nextReloadPollUtc)
            return false;

        nextReloadPollUtc = now + ReloadPollInterval;

        try
        {
            EnsureSeeded();
            var currentWriteUtc = File.GetLastWriteTimeUtc(configPath);
            if (currentWriteUtc == lastObservedRulesWriteUtc)
                return false;

            return Reload();
        }
        catch (Exception ex)
        {
            RememberCurrentRulesWriteTime();
            LastLoadStatus = $"Failed to check {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool SaveManifest(DialogYesNoRuleManifest manifest)
        => SaveManifest(DefaultPresetName, manifest);

    public bool SaveManifest(string presetName, DialogYesNoRuleManifest manifest)
    {
        try
        {
            manifest.Rules ??= [];
            NormalizeManifest(manifest);
            var path = EnsurePresetSeeded(presetName);
            WriteManifestToPath(path, manifest);
            if (!IsDefaultPreset(presetName))
            {
                LastLoadStatus = $"Saved {manifest.Rules.Count(x => x.Enabled)} active dialog rule(s) to preset {presetName} at {path}.";
                log.Information($"[ADS] {LastLoadStatus}");
                return true;
            }

            return Reload();
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to save dialog preset {presetName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool TryLoadManifest(string presetName, out DialogYesNoRuleManifest manifest, out string status)
    {
        manifest = new DialogYesNoRuleManifest();
        status = "Dialog preset was not loaded.";

        try
        {
            var path = EnsurePresetSeeded(presetName);
            return TryLoadManifestFromPath(path, out manifest, out status, persistMigrations: true);
        }
        catch (Exception ex)
        {
            status = $"Failed to load dialog preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryLoadDefaultCacheManifest(out DialogYesNoRuleManifest manifest, out string status)
    {
        manifest = new DialogYesNoRuleManifest();
        status = "DEFAULT dialog cache preset was not loaded.";

        try
        {
            EnsureSeeded();
            return TryLoadManifestFromPath(configPath, out manifest, out status, persistMigrations: false);
        }
        catch (Exception ex)
        {
            status = $"Failed to load DEFAULT dialog cache preset: {ex.Message}";
            return false;
        }
    }

    public bool TryDeletePreset(string presetName, out string status)
    {
        status = "Dialog preset was not deleted.";
        if (IsDefaultPreset(presetName))
        {
            status = "DEFAULT cannot be deleted.";
            return false;
        }

        try
        {
            var path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                status = $"Dialog preset {presetName} did not exist on disk.";
                return false;
            }

            File.Delete(path);
            status = $"Deleted dialog preset {presetName}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to delete dialog preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryImportManifestText(string text, out DialogYesNoRuleManifest manifest, out string status)
    {
        manifest = new DialogYesNoRuleManifest();
        status = "Clipboard dialog manifest import failed.";

        if (string.IsNullOrWhiteSpace(text))
        {
            status = "Clipboard was empty; no full-manifest import performed.";
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            try
            {
                trimmed = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            }
            catch
            {
                // Treat clipboard as raw JSON if it is not valid base64.
            }
        }

        return TryDeserializeManifest(trimmed, "<clipboard>", out manifest, out status);
    }

    public bool TryImportManifestFromPath(string path, out DialogYesNoRuleManifest manifest, out string status)
    {
        manifest = new DialogYesNoRuleManifest();
        status = "Disk dialog manifest import failed.";

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "Disk path was blank; no import performed.";
                return false;
            }

            if (!File.Exists(path))
            {
                status = $"Disk import path did not exist: {path}";
                return false;
            }

            return TryLoadManifestFromPath(path, out manifest, out status, persistMigrations: false);
        }
        catch (Exception ex)
        {
            status = $"Failed to import dialog manifest from {path}: {ex.Message}";
            return false;
        }
    }

    public bool TryExportManifestToPath(string path, DialogYesNoRuleManifest manifest, out string status)
    {
        status = "Disk dialog manifest export failed.";

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "Disk path was blank; no export performed.";
                return false;
            }

            WriteManifestToPath(path, manifest);
            status = $"Exported dialog manifest to {path}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to export dialog manifest to {path}: {ex.Message}";
            return false;
        }
    }

    public DialogYesNoRule? MatchRule(string promptText)
        => Current.Rules
            .Where(x => x.Enabled)
            .Where(x => Matches(x, promptText))
            .FirstOrDefault();

    public bool MatchesPrompt(DialogYesNoRule rule, string promptText)
        => Matches(rule, promptText);

    private void EnsureSeeded()
    {
        if (File.Exists(configPath))
            return;

        File.WriteAllText(configPath, GetDefaultJson());
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow - TimeSpan.FromDays(2));
        LastSyncStatus = "Default dialog rules config was missing, so ADS seeded a minimal built-in fallback until the botologyupdates cache refresh succeeds.";
        log.Warning($"[ADS] {LastSyncStatus}");
    }

    private string EnsurePresetSeeded(string presetName)
    {
        if (IsDefaultPreset(presetName))
        {
            EnsureSeeded();
            return configPath;
        }

        var path = GetPresetPath(presetName);
        if (File.Exists(path))
            return path;

        if (File.Exists(configPath))
        {
            File.Copy(configPath, path, overwrite: false);
            return path;
        }

        File.WriteAllText(path, GetDefaultJson());
        return path;
    }

    private void RememberCurrentRulesWriteTime()
    {
        try
        {
            if (File.Exists(configPath))
                lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
        }
        catch
        {
            // Best effort only.
        }
    }

    private bool TryLoadManifestFromPath(string path, out DialogYesNoRuleManifest manifest, out string status, bool persistMigrations)
    {
        manifest = new DialogYesNoRuleManifest();
        status = $"Failed to load dialog manifest from {path}.";

        try
        {
            var json = File.ReadAllText(path);
            if (!TryDeserializeManifest(json, path, out manifest, out status))
                return false;

            NormalizeManifest(manifest);

            status = $"Loaded {manifest.Rules.Count(x => x.Enabled)} active dialog rule(s) from {path}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to load dialog manifest from {path}: {ex.Message}";
            return false;
        }
    }

    private static bool TryDeserializeManifest(string json, string sourcePath, out DialogYesNoRuleManifest manifest, out string status)
    {
        manifest = new DialogYesNoRuleManifest();
        status = $"Failed to parse dialog manifest from {sourcePath}.";

        try
        {
            manifest = JsonSerializer.Deserialize<DialogYesNoRuleManifest>(json, JsonOptions) ?? new DialogYesNoRuleManifest();
            manifest.Rules ??= [];
            NormalizeManifest(manifest);
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to parse dialog manifest from {sourcePath}: {ex.Message}";
            return false;
        }
    }

    private static void WriteManifestToPath(string path, DialogYesNoRuleManifest manifest)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static bool Matches(DialogYesNoRule rule, string promptText)
    {
        if (string.IsNullOrWhiteSpace(rule.PromptPattern))
            return false;

        return string.Equals(rule.MatchMode, "Exact", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(promptText, rule.PromptPattern, StringComparison.OrdinalIgnoreCase)
            : promptText.Contains(rule.PromptPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static DialogYesNoRule CloneRule(DialogYesNoRule rule)
        => new()
        {
            Enabled = rule.Enabled,
            Addon = NormalizeAddon(rule.Addon),
            PromptPattern = rule.PromptPattern,
            MatchMode = rule.MatchMode,
            Response = rule.Response,
            Delay = Math.Max(0, rule.Delay),
            Notification = NormalizeOptional(rule.Notification),
            NotificationCB = NormalizeOptional(rule.NotificationCB),
            Notes = rule.Notes,
        };

    private static void NormalizeManifest(DialogYesNoRuleManifest manifest)
    {
        manifest.Description ??= string.Empty;
        manifest.Rules ??= [];

        foreach (var rule in manifest.Rules)
        {
            rule.Addon = NormalizeAddon(rule.Addon);
            rule.PromptPattern = NormalizeOptional(rule.PromptPattern);
            rule.MatchMode = string.IsNullOrWhiteSpace(rule.MatchMode) ? "Contains" : rule.MatchMode.Trim();
            rule.Response = string.IsNullOrWhiteSpace(rule.Response) ? "Yes" : rule.Response.Trim();
            rule.Delay = Math.Max(0, rule.Delay);
            rule.Notification = NormalizeOptional(rule.Notification);
            rule.NotificationCB = NormalizeOptional(rule.NotificationCB);
            rule.Notes = NormalizeOptional(rule.Notes);
        }
    }

    private static string NormalizeAddon(string? addon)
        => string.IsNullOrWhiteSpace(addon) ? "SelectYesno" : addon.Trim();

    private static string NormalizeOptional(string? value)
        => value?.Trim() ?? string.Empty;

    private static string GetDefaultJson()
        => """
{
  "schemaVersion": 1,
  "description": "Minimal built-in ADS dialog fallback. The live DEFAULT cache should normally be refreshed from botologyupdates.",
  "rules": [
    {
      "enabled": true,
      "addon": "SelectYesno",
      "promptPattern": "imperial identification key to deactivate the barrier",
      "matchMode": "Contains",
      "response": "Yes",
      "delay": 0,
      "notification": "",
      "notificationCB": "",
      "notes": "Keeper of the Lake barrier confirmation."
    }
  ]
}
""";
}
