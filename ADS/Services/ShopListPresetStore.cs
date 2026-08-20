using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ShopListPresetStore
{
    public const string DefaultPresetName = ShopListPresetStoreDefaults.DefaultPresetName;
    public const string FileName = "shop-lists.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginLog? log;
    private ShopListManifest manifest = NewManifest();

    public ShopListPresetStore(string configDirectory, IPluginLog? log = null)
    {
        this.log = log;
        ConfigPath = Path.Combine(configDirectory, FileName);
        Reload();
    }

    public string ConfigPath { get; }
    public string LastStatus { get; private set; } = string.Empty;
    public string ActivePresetName => manifest.ActivePresetName;
    public IReadOnlyList<ShopListPreset> Presets => manifest.Presets;
    public ShopListPreset ActivePreset => Get(manifest.ActivePresetName);

    public ShopListPreset Get(string? name)
        => manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, name))
           ?? manifest.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));

    public bool Select(string name, out string error)
        => Commit(candidate =>
        {
            var preset = candidate.Presets.FirstOrDefault(x => NamesEqual(x.Name, name));
            if (preset == null)
                throw new InvalidDataException($"Preset '{name}' was not found.");
            candidate.ActivePresetName = preset.Name;
        }, out error);

    public bool Create(string name, out string error)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Preset name is required.";
            return false;
        }

        if (manifest.Presets.Any(x => NamesEqual(x.Name, trimmed)))
        {
            error = $"Preset '{trimmed}' already exists.";
            return false;
        }

        return Commit(candidate =>
        {
            candidate.Presets.Add(new ShopListPreset { Name = trimmed });
            candidate.ActivePresetName = trimmed;
        }, out error);
    }

    public bool RenameActive(string newName, out string error)
    {
        var oldName = ActivePreset.Name;
        if (NamesEqual(oldName, DefaultPresetName))
        {
            error = "DEFAULT cannot be renamed.";
            return false;
        }

        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Preset name is required.";
            return false;
        }

        if (manifest.Presets.Any(x => !NamesEqual(x.Name, oldName) && NamesEqual(x.Name, trimmed)))
        {
            error = $"Preset '{trimmed}' already exists.";
            return false;
        }

        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => NamesEqual(x.Name, oldName));
            preset.Name = trimmed;
            candidate.ActivePresetName = trimmed;
        }, out error);
    }

    public bool DeleteActive(out string error)
    {
        var name = ActivePreset.Name;
        if (NamesEqual(name, DefaultPresetName))
        {
            error = "DEFAULT cannot be deleted.";
            return false;
        }

        return Commit(candidate =>
        {
            candidate.Presets.RemoveAll(x => NamesEqual(x.Name, name));
            candidate.ActivePresetName = DefaultPresetName;
        }, out error);
    }

    public bool SetItem(uint itemId, int quantity, out string error)
    {
        if (!ShopPurchaseRequest.TryCreate(itemId, quantity, out _, out error))
            return false;

        var activeName = ActivePreset.Name;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => NamesEqual(x.Name, activeName));
            var existing = preset.Items.FirstOrDefault(x => x.ItemId == itemId);
            if (existing == null)
                preset.Items.Add(new ShopListItem { ItemId = itemId, Quantity = quantity });
            else
                existing.Quantity = quantity;
        }, out error);
    }

    public bool RemoveItem(uint itemId, out string error)
    {
        var activeName = ActivePreset.Name;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => NamesEqual(x.Name, activeName));
            if (preset.Items.RemoveAll(x => x.ItemId == itemId) == 0)
                throw new InvalidDataException($"Item {itemId} was not in the active preset.");
        }, out error);
    }

    public bool ReplaceActiveItems(IReadOnlyDictionary<uint, int> items, out string error)
    {
        if (items.Count == 0)
        {
            error = "The imported shop list was empty; the active preset was not changed.";
            return false;
        }

        var activeName = ActivePreset.Name;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => NamesEqual(x.Name, activeName));
            preset.Items = items
                .Select(x => new ShopListItem { ItemId = x.Key, Quantity = x.Value })
                .ToList();
        }, out error);
    }

    public void Reload()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var candidate = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<ShopListManifest>(File.ReadAllText(ConfigPath), JsonOptions)
                  ?? throw new InvalidDataException("Shop-list preset JSON was empty.")
                : NewManifest();
            NormalizeAndValidate(candidate);
            Write(candidate);
            manifest = candidate;
            LastStatus = $"Loaded {manifest.Presets.Count} shop-list preset(s).";
        }
        catch (Exception ex)
        {
            var candidate = NewManifest();
            try
            {
                Write(candidate);
                manifest = candidate;
            }
            catch (Exception saveEx)
            {
                log?.Warning(saveEx, "[ADS][ShopLists] Could not save a replacement DEFAULT preset.");
            }

            LastStatus = $"Shop-list preset load failed; reset to DEFAULT: {ex.Message}";
            log?.Warning(ex, $"[ADS][ShopLists] {LastStatus}");
        }
    }

    private bool Commit(Action<ShopListManifest> mutation, out string error)
    {
        try
        {
            var candidate = Clone(manifest);
            mutation(candidate);
            NormalizeAndValidate(candidate);
            Write(candidate);
            manifest = candidate;
            error = string.Empty;
            LastStatus = $"Saved {manifest.Presets.Count} shop-list preset(s).";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Shop-list preset save failed: {ex.Message}";
            LastStatus = error;
            log?.Warning(ex, $"[ADS][ShopLists] {error}");
            return false;
        }
    }

    private void Write(ShopListManifest candidate)
        => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(candidate, JsonOptions), new UTF8Encoding(false));

    private static ShopListManifest Clone(ShopListManifest value)
        => new()
        {
            Version = value.Version,
            ActivePresetName = value.ActivePresetName,
            Presets = value.Presets.Select(preset => new ShopListPreset
            {
                Name = preset.Name,
                Items = preset.Items.Select(item => new ShopListItem
                {
                    ItemId = item.ItemId,
                    Quantity = item.Quantity,
                }).ToList(),
            }).ToList(),
        };

    private static void NormalizeAndValidate(ShopListManifest value)
    {
        if (value.Version != 1)
            throw new InvalidDataException($"Unsupported shop-list schema version {value.Version}.");

        value.Presets ??= [];
        foreach (var preset in value.Presets)
        {
            preset.Name = preset.Name?.Trim() ?? string.Empty;
            preset.Items ??= [];
            var consolidated = new Dictionary<uint, int>();
            foreach (var item in preset.Items)
            {
                if (!ShopPurchaseRequest.TryCreate(item.ItemId, item.Quantity, out _, out var error))
                    throw new InvalidDataException($"Preset '{preset.Name}' contains an invalid row: {error}");
                consolidated[item.ItemId] = checked(consolidated.GetValueOrDefault(item.ItemId) + item.Quantity);
                if (consolidated[item.ItemId] > ShopPurchaseRequest.MaximumQuantity)
                    throw new InvalidDataException($"Preset '{preset.Name}' exceeds {ShopPurchaseRequest.MaximumQuantity} for item {item.ItemId}.");
            }

            preset.Items = consolidated
                .OrderBy(x => x.Key)
                .Select(x => new ShopListItem { ItemId = x.Key, Quantity = x.Value })
                .ToList();
        }

        if (value.Presets.Any(x => string.IsNullOrEmpty(x.Name)))
            throw new InvalidDataException("Every shop-list preset requires a name.");
        if (value.Presets.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("Shop-list preset names must be unique.");
        if (!value.Presets.Any(x => NamesEqual(x.Name, DefaultPresetName)))
            value.Presets.Insert(0, new ShopListPreset { Name = DefaultPresetName });

        var active = value.Presets.FirstOrDefault(x => NamesEqual(x.Name, value.ActivePresetName))
                     ?? value.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));
        value.ActivePresetName = active.Name;
    }

    private static ShopListManifest NewManifest()
        => new()
        {
            ActivePresetName = DefaultPresetName,
            Presets = [new ShopListPreset { Name = DefaultPresetName }],
        };

    private static bool NamesEqual(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
