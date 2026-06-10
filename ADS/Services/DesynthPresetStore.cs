using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DesynthPresetStore
{
    public const string DefaultPresetName = "DEFAULT";
    public const string FileName = "desynth-presets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginLog? log;
    private DesynthPresetManifest manifest = new();

    public DesynthPresetStore(string configDirectory)
        : this(configDirectory, null)
    {
    }

    public DesynthPresetStore(string configDirectory, IPluginLog? log)
    {
        this.log = log;
        ConfigPath = Path.Combine(configDirectory, FileName);
        Reload();
    }

    public string ConfigPath { get; }
    public string LastStatus { get; private set; } = string.Empty;
    public IReadOnlyList<DesynthPreset> Presets => manifest.Presets;

    public DesynthPreset Get(string? name)
        => manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, name))
           ?? manifest.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));

    public bool Create(string name, string description, out string error)
    {
        error = ValidateNewName(name);
        if (!string.IsNullOrEmpty(error))
            return false;

        manifest.Presets.Add(new DesynthPreset { Name = name.Trim(), Description = description?.Trim() ?? string.Empty });
        return Save(out error);
    }

    public bool Rename(string oldName, string newName, out string error)
    {
        if (NamesEqual(oldName, DefaultPresetName))
        {
            error = "DEFAULT cannot be renamed.";
            return false;
        }

        var preset = manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, oldName));
        if (preset == null)
        {
            error = $"Preset '{oldName}' was not found.";
            return false;
        }

        error = ValidateNewName(newName);
        if (!string.IsNullOrEmpty(error))
            return false;

        preset.Name = newName.Trim();
        return Save(out error);
    }

    public bool Delete(string name, out string error)
    {
        if (NamesEqual(name, DefaultPresetName))
        {
            error = "DEFAULT cannot be deleted.";
            return false;
        }

        var removed = manifest.Presets.RemoveAll(x => NamesEqual(x.Name, name));
        if (removed == 0)
        {
            error = $"Preset '{name}' was not found.";
            return false;
        }

        return Save(out error);
    }

    public bool AddItem(string presetName, uint itemId, out string error)
        => MutateItem(presetName, itemId, add: true, out error);

    public bool RemoveItem(string presetName, uint itemId, out string error)
        => MutateItem(presetName, itemId, add: false, out error);

    public string ExportRaw()
        => JsonSerializer.Serialize(manifest, JsonOptions);

    public string ExportBase64()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(ExportRaw()));

    public bool ImportRaw(string json, out string error)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<DesynthPresetManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Preset JSON was empty.");
            NormalizeAndValidate(candidate);
            manifest = candidate;
            return Save(out error);
        }
        catch (Exception ex)
        {
            error = $"Preset import failed: {ex.Message}";
            LastStatus = error;
            return false;
        }
    }

    public bool ImportBase64(string base64, out string error)
    {
        try
        {
            return ImportRaw(Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim())), out error);
        }
        catch (Exception ex)
        {
            error = $"Preset base64 import failed: {ex.Message}";
            LastStatus = error;
            return false;
        }
    }

    public void Reload()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            if (File.Exists(ConfigPath))
            {
                manifest = JsonSerializer.Deserialize<DesynthPresetManifest>(File.ReadAllText(ConfigPath), JsonOptions)
                    ?? new DesynthPresetManifest();
            }

            NormalizeAndValidate(manifest);
            Save(out _);
            LastStatus = $"Loaded {manifest.Presets.Count} desynthesis preset(s).";
        }
        catch (Exception ex)
        {
            manifest = NewManifest();
            Save(out _);
            LastStatus = $"Preset load failed; reset to DEFAULT: {ex.Message}";
            log?.Warning(ex, $"[ADS][Desynth] {LastStatus}");
        }
    }

    private bool MutateItem(string presetName, uint itemId, bool add, out string error)
    {
        var preset = manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, presetName));
        if (preset == null)
        {
            error = $"Preset '{presetName}' was not found.";
            return false;
        }

        var normalized = DesynthPolicyService.NormalizeBaseItemId(itemId);
        if (normalized == 0)
        {
            error = "Item ID must be greater than zero.";
            return false;
        }

        if (add)
        {
            if (!preset.ItemIds.Contains(normalized))
                preset.ItemIds.Add(normalized);
        }
        else
        {
            preset.ItemIds.RemoveAll(x => x == normalized);
        }

        return Save(out error);
    }

    private bool Save(out string error)
    {
        try
        {
            NormalizeAndValidate(manifest);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            error = string.Empty;
            LastStatus = $"Saved {manifest.Presets.Count} desynthesis preset(s).";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Preset save failed: {ex.Message}";
            LastStatus = error;
            log?.Warning(ex, $"[ADS][Desynth] {error}");
            return false;
        }
    }

    private string ValidateNewName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return "Preset name is required.";
        if (manifest.Presets.Any(x => NamesEqual(x.Name, trimmed)))
            return $"Preset '{trimmed}' already exists.";
        return string.Empty;
    }

    private static void NormalizeAndValidate(DesynthPresetManifest value)
    {
        if (value.Version != 1)
            throw new InvalidDataException($"Unsupported preset schema version {value.Version}.");

        value.Presets ??= [];
        foreach (var preset in value.Presets)
        {
            preset.Name = preset.Name?.Trim() ?? string.Empty;
            preset.Description = preset.Description?.Trim() ?? string.Empty;
            preset.ItemIds = (preset.ItemIds ?? [])
                .Select(DesynthPolicyService.NormalizeBaseItemId)
                .Where(x => x > 0)
                .Distinct()
                .Order()
                .ToList();
        }

        if (value.Presets.Any(x => string.IsNullOrEmpty(x.Name)))
            throw new InvalidDataException("Every preset requires a name.");
        if (value.Presets.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("Preset names must be unique.");
        if (!value.Presets.Any(x => NamesEqual(x.Name, DefaultPresetName)))
            value.Presets.Insert(0, new DesynthPreset { Name = DefaultPresetName });
    }

    private static DesynthPresetManifest NewManifest()
        => new() { Presets = [new DesynthPreset { Name = DefaultPresetName }] };

    private static bool NamesEqual(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
