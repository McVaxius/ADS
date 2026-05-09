using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DialogYesNoRuleService
{
    private const string FileName = "dialog-yesno-rules.json";
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
    private readonly string bundledPath;
    private DateTime lastObservedRulesWriteUtc;
    private DateTime nextReloadPollUtc;

    public DialogYesNoRuleService(IPluginLog log, string configDirectory, string? assemblyDirectory, string initialSyncStatus)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        configPath = Path.Combine(configDirectory, FileName);
        bundledPath = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? string.Empty
            : Path.Combine(assemblyDirectory, FileName);
        LastSyncStatus = initialSyncStatus;

        EnsureSeeded();
        Reload();
    }

    public string ConfigPath
        => configPath;

    public string LastLoadStatus { get; private set; } = "Dialog rules not loaded yet.";

    public string LastSyncStatus { get; private set; } = "Packaged dialog sync not checked yet.";

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

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            var json = File.ReadAllText(configPath);
            var manifest = JsonSerializer.Deserialize<DialogYesNoRuleManifest>(json, JsonOptions) ?? new DialogYesNoRuleManifest();
            manifest.Rules ??= [];
            NormalizeManifest(manifest);

            Current = manifest;
            lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
            LastLoadStatus = $"Loaded {ActiveRuleCount} active dialog rule(s) from {configPath}.";
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
    {
        try
        {
            manifest.Rules ??= [];
            NormalizeManifest(manifest);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(configPath, json);
            return Reload();
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to save {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
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

        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, configPath, overwrite: false);
            LastSyncStatus = $"Default dialog rules config was missing, so ADS re-seeded it from {bundledPath}.";
            log.Information($"[ADS] {LastSyncStatus}");
            return;
        }

        File.WriteAllText(configPath, GetDefaultJson());
        LastSyncStatus = $"Default dialog rules config was missing, so ADS re-seeded it from built-in defaults.";
        log.Warning($"[ADS] {LastSyncStatus}");
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
  "description": "Human-edited ADS dialog rules. These are global, not duty-scoped. Default Addon is SelectYesno; processing scope follows the ADS Settings dialog-rule toggle.",
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
