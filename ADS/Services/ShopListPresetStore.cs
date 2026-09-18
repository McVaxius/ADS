using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ShopListPresetStore
{
    public const string DefaultPresetName = ShopListPresetStoreDefaults.DefaultPresetName;
    public const string FileName = "shop-lists.json";
    private const int CurrentVersion = 2;
    private const int ShareVersion = 1;
    private const int MaximumShareLength = 256 * 1024;
    private const int MaximumPresetNameLength = 80;
    public const int MaximumRowsPerPreset = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPluginLog? log;
    private ShopListManifest manifest = NewManifest();
    private bool loadFailed;

    public ShopListPresetStore(string configDirectory, IPluginLog? log = null)
    {
        this.log = log;
        ConfigPath = Path.Combine(configDirectory, FileName);
        Reload();
    }

    public string ConfigPath { get; }
    public string LastStatus { get; private set; } = string.Empty;
    public Guid ActivePresetId => manifest.ActivePresetId;
    public string ActivePresetName => ActivePreset.Name;
    public IReadOnlyList<ShopListPreset> Presets => manifest.Presets;
    public ShopListPreset ActivePreset => Get(manifest.ActivePresetId);

    public IReadOnlyDictionary<Guid, long> GetOrderProgress(ulong characterId, Guid presetId)
        => new Dictionary<Guid, long>(manifest.CharacterOrders.FirstOrDefault(order =>
            order.CharacterId == characterId && order.PresetId == presetId)?.CreditedQuantities ?? []);

    public bool SaveOrderProgress(ulong characterId, Guid presetId, IReadOnlyDictionary<Guid, long> progress, out string error)
        => Commit(candidate =>
        {
            if (characterId == 0)
                throw new InvalidDataException("A logged-in character is required for an order.");
            var preset = candidate.Presets.Single(preset => preset.PresetId == presetId);
            candidate.CharacterOrders.RemoveAll(order => order.CharacterId == characterId && order.PresetId == presetId);
            candidate.CharacterOrders.Add(new ShopListCharacterOrder
            {
                CharacterId = characterId,
                PresetId = presetId,
                CreditedQuantities = progress.Where(pair => preset.Items.Any(row => row.RowId == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => Math.Max(0, pair.Value)),
            });
        }, out error);

    public ShopListPreset Get(Guid presetId)
        => manifest.Presets.FirstOrDefault(x => x.PresetId == presetId)
           ?? manifest.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));

    public ShopListPreset Get(string? name)
        => manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, name))
           ?? manifest.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));

    public bool Select(Guid presetId, out string error)
        => Commit(candidate =>
        {
            if (!candidate.Presets.Any(x => x.PresetId == presetId))
                throw new InvalidDataException($"Preset '{presetId:D}' was not found.");
            candidate.ActivePresetId = presetId;
        }, out error);

    public bool Select(string name, out string error)
    {
        var preset = manifest.Presets.FirstOrDefault(x => NamesEqual(x.Name, name));
        if (preset == null)
        {
            error = $"Preset '{name}' was not found.";
            return false;
        }

        return Select(preset.PresetId, out error);
    }

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
            var preset = NewPreset(trimmed);
            candidate.Presets.Add(preset);
            candidate.ActivePresetId = preset.PresetId;
        }, out error);
    }

    public bool RenameActive(string newName, out string error)
    {
        var activeId = ActivePreset.PresetId;
        var active = ActivePreset;
        if (NamesEqual(active.Name, DefaultPresetName))
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

        if (manifest.Presets.Any(x => x.PresetId != activeId && NamesEqual(x.Name, trimmed)))
        {
            error = $"Preset '{trimmed}' already exists.";
            return false;
        }

        return Commit(candidate =>
        {
            candidate.Presets.First(x => x.PresetId == activeId).Name = trimmed;
        }, out error);
    }

    internal bool AddExample(ShopListExample example, out string error)
    {
        if (manifest.Presets.Any(x => NamesEqual(x.Name, example.Name)))
        {
            error = $"Preset '{example.Name}' already exists; rename it before adding another example.";
            return false;
        }

        return Commit(candidate =>
        {
            var preset = NewPreset(example.Name);
            preset.Mode = ShopListMode.TargetedRefill;
            preset.CurrencyKind = ShopCurrencyKind.Tomestone;
            preset.CurrencyItemId = 28;
            preset.Items = example.Items.Select(item => new ShopListItem
            {
                RowId = Guid.NewGuid(),
                ItemId = item.ItemId,
                TriggerBelow = item.Quantity,
                RefillToAtLeast = item.Quantity,
                Repeatable = false,
                OwnershipScope = ShopListOwnershipScope.InventoryOnly,
            }).ToList();
            candidate.Presets.Add(preset);
            candidate.ActivePresetId = preset.PresetId;
        }, out error);
    }

    public bool DeleteActive(out string error)
    {
        var active = ActivePreset;
        if (NamesEqual(active.Name, DefaultPresetName))
        {
            error = "DEFAULT cannot be deleted.";
            return false;
        }

        return Commit(candidate =>
        {
            candidate.Presets.RemoveAll(x => x.PresetId == active.PresetId);
            candidate.ActivePresetId = candidate.Presets.First(x => NamesEqual(x.Name, DefaultPresetName)).PresetId;
        }, out error);
    }

    public bool ConfigureActive(
        ShopListMode mode,
        ShopCurrencyKind currencyKind,
        uint currencyItemId,
        long currencyThreshold,
        out string error)
    {
        var activeId = ActivePreset.PresetId;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => x.PresetId == activeId);
            var rotateRows = preset.Mode != mode
                             || preset.CurrencyKind != currencyKind
                             || preset.CurrencyItemId != currencyItemId;
            preset.Mode = mode;
            preset.CurrencyKind = currencyKind;
            preset.CurrencyItemId = currencyItemId;
            preset.CurrencyThreshold = currencyThreshold;
            if (rotateRows)
            {
                foreach (var item in preset.Items)
                    item.RowId = Guid.NewGuid();
            }
        }, out error);
    }

    public bool SetItem(
        uint itemId,
        int triggerBelow,
        int refillToAtLeast,
        bool repeatable,
        ShopListOwnershipScope ownershipScope,
        out string error)
    {
        var activeId = ActivePreset.PresetId;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => x.PresetId == activeId);
            var existing = preset.Items.FirstOrDefault(x => x.ItemId == itemId);
            if (existing == null)
            {
                if (preset.Items.Count >= MaximumRowsPerPreset)
                    throw new InvalidDataException($"A shop-list preset cannot contain more than {MaximumRowsPerPreset} rows.");
                preset.Items.Add(new ShopListItem
                {
                    RowId = Guid.NewGuid(),
                    ItemId = itemId,
                    TriggerBelow = triggerBelow,
                    RefillToAtLeast = refillToAtLeast,
                    Repeatable = repeatable,
                    OwnershipScope = ownershipScope,
                });
            }
            else
            {
                if (existing.TriggerBelow != triggerBelow
                    || existing.RefillToAtLeast != refillToAtLeast
                    || existing.Repeatable != repeatable
                    || existing.OwnershipScope != ownershipScope)
                {
                    existing.RowId = Guid.NewGuid();
                }
                existing.TriggerBelow = triggerBelow;
                existing.RefillToAtLeast = refillToAtLeast;
                existing.Repeatable = repeatable;
                existing.OwnershipScope = ownershipScope;
            }
        }, out error);
    }

    public bool UpdateItem(
        Guid rowId,
        int triggerBelow,
        int refillToAtLeast,
        bool repeatable,
        ShopListOwnershipScope ownershipScope,
        out string error)
    {
        var activeId = ActivePreset.PresetId;
        return Commit(candidate =>
        {
            var row = candidate.Presets.First(x => x.PresetId == activeId).Items.FirstOrDefault(x => x.RowId == rowId)
                      ?? throw new InvalidDataException($"Row '{rowId:D}' was not in the active preset.");
            if (row.TriggerBelow != triggerBelow
                || row.RefillToAtLeast != refillToAtLeast
                || row.Repeatable != repeatable
                || row.OwnershipScope != ownershipScope)
            {
                row.RowId = Guid.NewGuid();
            }
            row.TriggerBelow = triggerBelow;
            row.RefillToAtLeast = refillToAtLeast;
            row.Repeatable = repeatable;
            row.OwnershipScope = ownershipScope;
        }, out error);
    }

    public bool RemoveItem(Guid rowId, out string error)
    {
        var activeId = ActivePreset.PresetId;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => x.PresetId == activeId);
            if (preset.Items.RemoveAll(x => x.RowId == rowId) == 0)
                throw new InvalidDataException($"Row '{rowId:D}' was not in the active preset.");
        }, out error);
    }

    public bool ReplaceActiveItems(IReadOnlyDictionary<uint, int> items, out string error)
    {
        if (items.Count == 0)
        {
            error = "The imported shop list was empty; the active preset was not changed.";
            return false;
        }

        var activeId = ActivePreset.PresetId;
        return Commit(candidate =>
        {
            var preset = candidate.Presets.First(x => x.PresetId == activeId);
            preset.Items = items.Select(x => new ShopListItem
            {
                RowId = Guid.NewGuid(),
                ItemId = x.Key,
                TriggerBelow = x.Value,
                RefillToAtLeast = x.Value,
                Repeatable = true,
                OwnershipScope = ShopListOwnershipScope.InventoryAndRetainers,
            }).ToList();
        }, out error);
    }

    public string ExportActiveBase64()
    {
        var envelope = new ShopListPresetShare
        {
            Version = ShareVersion,
            Preset = ClonePreset(ActivePreset),
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)));
    }

    public bool ImportBase64(string encoded, out string error)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumShareLength)
        {
            error = "The Base64 shop-list preset is empty or too large.";
            return false;
        }

        ShopListPresetShare share;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
            share = JsonSerializer.Deserialize<ShopListPresetShare>(json, JsonOptions)
                    ?? throw new InvalidDataException("The decoded preset was empty.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            error = $"Shop-list preset import failed: {ex.Message}";
            return false;
        }

        if (share.Version != ShareVersion || share.Preset == null)
        {
            error = "The decoded shop-list preset version is missing or unsupported.";
            return false;
        }
        if (manifest.Presets.Any(x => x.PresetId == share.Preset.PresetId))
        {
            error = $"Preset ID '{share.Preset.PresetId:D}' already exists; the existing preset was not changed.";
            return false;
        }
        if (manifest.Presets.Any(x => NamesEqual(x.Name, share.Preset.Name)))
        {
            error = $"Preset '{share.Preset.Name}' already exists; rename it before importing this copy.";
            return false;
        }

        return Commit(candidate =>
        {
            var imported = ClonePreset(share.Preset);
            candidate.Presets.Add(imported);
            candidate.ActivePresetId = imported.PresetId;
        }, out error);
    }

    public void Reload()
    {
        ShopListManifest? loaded = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var candidate = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<ShopListManifest>(File.ReadAllText(ConfigPath), JsonOptions)
                  ?? throw new InvalidDataException("Shop-list preset JSON was empty.")
                : NewManifest();
            NormalizeAndValidate(candidate);
            loaded = Clone(candidate);
            InitializeRelicOrders(candidate);
            NormalizeAndValidate(candidate);
            Write(candidate);
            manifest = candidate;
            loadFailed = false;
            LastStatus = $"Loaded {manifest.Presets.Count} shop-list preset(s).";
        }
        catch (Exception ex)
        {
            if (loaded != null)
                manifest = loaded;
            loadFailed = true;
            LastStatus = $"Shop-list preset load/initialization failed; existing lists were preserved. Reload after resolving the error: {ex.Message}";
            log?.Warning(ex, $"[ADS][ShopLists] {LastStatus}");
        }
    }

    private bool Commit(Action<ShopListManifest> mutation, out string error)
    {
        try
        {
            if (loadFailed)
                throw new InvalidDataException("Resolve the shop-list load error and reload before saving.");
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
        => ObjectRuleShardStore.WriteJsonAtomic(ConfigPath, JsonSerializer.Serialize(candidate, JsonOptions));

    private static ShopListManifest Clone(ShopListManifest value)
        => new()
        {
            Version = value.Version,
            ActivePresetId = value.ActivePresetId,
            ActivePresetName = value.ActivePresetName,
            Presets = value.Presets.Select(ClonePreset).ToList(),
            RelicOrdersInitialized = value.RelicOrdersInitialized,
            CharacterOrders = value.CharacterOrders.Select(order => new ShopListCharacterOrder
            {
                CharacterId = order.CharacterId,
                PresetId = order.PresetId,
                CreditedQuantities = new(order.CreditedQuantities),
            }).ToList(),
        };

    private static ShopListPreset ClonePreset(ShopListPreset preset)
        => new()
        {
            PresetId = preset.PresetId,
            Name = preset.Name,
            Mode = preset.Mode,
            CurrencyKind = preset.CurrencyKind,
            CurrencyItemId = preset.CurrencyItemId,
            CurrencyThreshold = preset.CurrencyThreshold,
            Items = preset.Items.Select(item => new ShopListItem
            {
                RowId = item.RowId,
                ItemId = item.ItemId,
                TriggerBelow = item.TriggerBelow,
                RefillToAtLeast = item.RefillToAtLeast,
                Repeatable = item.Repeatable,
                OwnershipScope = item.OwnershipScope,
                Quantity = item.Quantity,
            }).ToList(),
        };

    private static void NormalizeAndValidate(ShopListManifest value)
    {
        if (value.Version == 1)
            MigrateVersionOne(value);
        if (value.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported shop-list schema version {value.Version}.");

        value.Presets ??= [];
        value.CharacterOrders ??= [];
        foreach (var preset in value.Presets)
        {
            if (preset.PresetId == Guid.Empty)
                preset.PresetId = Guid.NewGuid();
            preset.Name = preset.Name?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(preset.Mode))
                throw new InvalidDataException($"Preset '{preset.Name}' has an unsupported mode.");
            if (!Enum.IsDefined(preset.CurrencyKind))
                throw new InvalidDataException($"Preset '{preset.Name}' has an unsupported currency kind.");
            ValidateCurrencyIdentity(preset.Name, preset.CurrencyKind, preset.CurrencyItemId);
            if (preset.CurrencyThreshold < 0)
                throw new InvalidDataException($"Preset '{preset.Name}' has a negative currency threshold.");

            preset.Items ??= [];
            if (preset.Items.Count > MaximumRowsPerPreset)
                throw new InvalidDataException($"Preset '{preset.Name}' exceeds {MaximumRowsPerPreset} rows.");
            foreach (var item in preset.Items)
            {
                if (item.RowId == Guid.Empty)
                    item.RowId = Guid.NewGuid();
                if (item.Quantity.HasValue)
                {
                    item.TriggerBelow = item.Quantity.Value;
                    item.RefillToAtLeast = item.Quantity.Value;
                    item.Quantity = null;
                }
                ValidateRow(preset.Name, item);
                if (preset.Mode == ShopListMode.FillOrderOverMultipleRuns)
                {
                    item.TriggerBelow = item.RefillToAtLeast;
                    item.Repeatable = false;
                }
            }

            if (preset.Items.GroupBy(x => x.RowId).Any(x => x.Count() > 1))
                throw new InvalidDataException($"Preset '{preset.Name}' contains duplicate row IDs.");
            if (preset.Items.GroupBy(x => x.ItemId).Any(x => x.Count() > 1))
                throw new InvalidDataException($"Preset '{preset.Name}' contains duplicate item rows.");
        }

        if (value.Presets.Any(x => string.IsNullOrEmpty(x.Name)))
            throw new InvalidDataException("Every shop-list preset requires a name.");
        if (value.Presets.Any(x => x.Name.Length > MaximumPresetNameLength))
            throw new InvalidDataException($"Shop-list preset names cannot exceed {MaximumPresetNameLength} characters.");
        if (value.Presets.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("Shop-list preset names must be unique.");
        if (value.Presets.GroupBy(x => x.PresetId).Any(x => x.Count() > 1))
            throw new InvalidDataException("Shop-list preset IDs must be unique.");
        if (!value.Presets.Any(x => NamesEqual(x.Name, DefaultPresetName)))
            value.Presets.Insert(0, NewPreset(DefaultPresetName));

        var active = value.Presets.FirstOrDefault(x => x.PresetId == value.ActivePresetId)
                     ?? value.Presets.First(x => NamesEqual(x.Name, DefaultPresetName));
        value.ActivePresetId = active.PresetId;
        value.ActivePresetName = null;
    }

    private static void MigrateVersionOne(ShopListManifest value)
    {
        value.Presets ??= [];
        foreach (var preset in value.Presets)
        {
            preset.PresetId = Guid.NewGuid();
            preset.Mode = ShopListMode.TargetedRefill;
            preset.CurrencyKind = ShopCurrencyKind.Gil;
            preset.CurrencyItemId = 1;
            preset.Items ??= [];
            foreach (var item in preset.Items)
            {
                var quantity = item.Quantity ?? item.RefillToAtLeast;
                item.RowId = Guid.NewGuid();
                item.TriggerBelow = quantity;
                item.RefillToAtLeast = quantity;
                item.Repeatable = true;
                item.OwnershipScope = ShopListOwnershipScope.InventoryAndRetainers;
                item.Quantity = null;
            }
        }

        var active = value.Presets.FirstOrDefault(x => NamesEqual(x.Name, value.ActivePresetName));
        value.ActivePresetId = active?.PresetId ?? Guid.Empty;
        value.ActivePresetName = null;
        value.Version = CurrentVersion;
    }

    private static void ValidateRow(string presetName, ShopListItem item)
    {
        if (item.ItemId == 0)
            throw new InvalidDataException($"Preset '{presetName}' contains a row with item ID zero.");
        if (item.TriggerBelow is < 1 or > ShopPurchaseRequest.MaximumQuantity)
            throw new InvalidDataException($"Preset '{presetName}' row {item.ItemId} trigger must be between 1 and {ShopPurchaseRequest.MaximumQuantity}.");
        if (item.RefillToAtLeast < item.TriggerBelow || item.RefillToAtLeast > ShopPurchaseRequest.MaximumQuantity)
            throw new InvalidDataException($"Preset '{presetName}' row {item.ItemId} refill target must be at least its trigger and no more than {ShopPurchaseRequest.MaximumQuantity}.");
        if (!Enum.IsDefined(item.OwnershipScope))
            throw new InvalidDataException($"Preset '{presetName}' row {item.ItemId} has an unsupported ownership scope.");
    }

    private static void ValidateCurrencyIdentity(string presetName, ShopCurrencyKind kind, uint itemId)
    {
        if (kind == ShopCurrencyKind.Gil && itemId != 1)
            throw new InvalidDataException($"Preset '{presetName}' must use currency item ID 1 for gil.");
        if (kind == ShopCurrencyKind.FreeCompanyCredit && itemId != 0)
            throw new InvalidDataException($"Preset '{presetName}' must use currency item ID 0 for Free Company credits.");
        if (kind is not (ShopCurrencyKind.Gil or ShopCurrencyKind.FreeCompanyCredit) && itemId == 0)
            throw new InvalidDataException($"Preset '{presetName}' requires a nonzero currency item ID.");
    }

    private static ShopListManifest NewManifest()
    {
        var preset = NewPreset(DefaultPresetName);
        return new ShopListManifest
        {
            Version = CurrentVersion,
            ActivePresetId = preset.PresetId,
            Presets = [preset],
        };
    }

    private static void InitializeRelicOrders(ShopListManifest value)
    {
        if (value.RelicOrdersInitialized)
            return;
        AddRelicOrder(value, "a8de1000-0000-4000-8000-000000000001", "ARR Zodiac — Poetics",
            [(6267, 1), (6268, 3), (7885, 3), (9540, 4)]);
        AddRelicOrder(value, "a8de1000-0000-4000-8000-000000000002", "HW Anima — Poetics",
            [(13582, 10), (13584, 10), (13586, 10), (13588, 10), (14899, 5), (15840, 60), (16064, 50), (16933, 15), (16934, 1)]);
        value.RelicOrdersInitialized = true;
    }

    private static void AddRelicOrder(ShopListManifest value, string id, string name, (uint ItemId, int Target)[] rows)
    {
        var presetId = Guid.Parse(id);
        if (value.Presets.Any(preset => preset.PresetId == presetId))
            return;
        var displayName = name;
        for (var suffix = 2; value.Presets.Any(preset => NamesEqual(preset.Name, displayName)); suffix++)
            displayName = $"{name} ({suffix})";
        value.Presets.Add(new ShopListPreset
        {
            PresetId = presetId,
            Name = displayName,
            Mode = ShopListMode.FillOrderOverMultipleRuns,
            CurrencyKind = ShopCurrencyKind.Tomestone,
            CurrencyItemId = 28,
            Items = rows.Select(row => new ShopListItem
            {
                RowId = Guid.Parse($"a8de2000-0000-4000-8000-{row.ItemId:000000000000}"),
                ItemId = row.ItemId,
                TriggerBelow = row.Target,
                RefillToAtLeast = row.Target,
                Repeatable = false,
                OwnershipScope = ShopListOwnershipScope.InventoryOnly,
            }).ToList(),
        });
    }

    private static ShopListPreset NewPreset(string name)
        => new()
        {
            PresetId = Guid.NewGuid(),
            Name = name,
            CurrencyKind = ShopCurrencyKind.Gil,
            CurrencyItemId = 1,
        };

    private static bool NamesEqual(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class ShopListPresetShare
    {
        public int Version { get; set; }
        public ShopListPreset? Preset { get; set; }
    }
}
