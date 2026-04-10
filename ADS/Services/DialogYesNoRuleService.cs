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

    public DialogYesNoRuleService(IPluginLog log, string configDirectory, string? assemblyDirectory)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        configPath = Path.Combine(configDirectory, FileName);
        bundledPath = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? string.Empty
            : Path.Combine(assemblyDirectory, FileName);

        EnsureSeeded();
        Reload();
    }

    public string ConfigPath
        => configPath;

    public string LastLoadStatus { get; private set; } = "Dialog rules not loaded yet.";

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

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            var json = File.ReadAllText(configPath);
            var manifest = JsonSerializer.Deserialize<DialogYesNoRuleManifest>(json, JsonOptions) ?? new DialogYesNoRuleManifest();
            manifest.Rules ??= [];

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

    private void EnsureSeeded()
    {
        if (File.Exists(configPath))
            return;

        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, configPath, overwrite: false);
            return;
        }

        File.WriteAllText(configPath, GetDefaultJson());
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
            PromptPattern = rule.PromptPattern,
            MatchMode = rule.MatchMode,
            Response = rule.Response,
            Notes = rule.Notes,
        };

    private static string GetDefaultJson()
        => """
{
  "schemaVersion": 1,
  "description": "Human-edited ADS SelectYesno rules. These are global, not duty-scoped. Match prompt text and choose whether ADS should click Yes or No when it owns a supported duty.",
  "rules": [
    {
      "enabled": true,
      "promptPattern": "imperial identification key to deactivate the barrier",
      "matchMode": "Contains",
      "response": "Yes",
      "notes": "Keeper of the Lake barrier confirmation."
    }
  ]
}
""";
}
