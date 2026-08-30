using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DutyCatalogService
{
    private const string MaturityFileName = "duty-maturity.json";
    internal const string DefaultSupportNote = DutyMaturityCatalog.DefaultSupportNote;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<DutyClearanceStatus>(),
            new JsonStringEnumConverter<DutySupportLevel>(),
        },
    };

    private readonly List<DutyCatalogEntry> entries = [];
    private readonly Dictionary<uint, DutyCatalogEntry> entriesByCfc = [];
    private readonly Dictionary<uint, DutyCatalogEntry> entriesByTerritory = [];
    private readonly IPluginLog log;
    private readonly string maturityPath;

    public DutyCatalogService(IDataManager dataManager, IPluginLog log, string configDirectory)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        maturityPath = Path.Combine(configDirectory, MaturityFileName);

        var contentFinderSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        var englishSheet = dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
        var questSheet = dataManager.GetExcelSheet<Quest>();
        var englishQuestSheet = dataManager.GetExcelSheet<Quest>(ClientLanguage.English);
        if (contentFinderSheet is null)
        {
            log.Warning("[ADS] ContentFinderCondition sheet was unavailable; ADS duty catalog is empty.");
            return;
        }

        foreach (var row in contentFinderSheet)
        {
            var isQuestBattle = TryGetLinkedQuestId(row, out var linkedQuestId);
            var contentType = row.ContentType.ValueNullable;
            if (!isQuestBattle && contentType is null)
                continue;

            if (row.TerritoryType.ValueNullable is null || row.ContentMemberType.ValueNullable is null)
                continue;

            var partySize = row.ContentMemberType.Value.TanksPerParty
                + row.ContentMemberType.Value.HealersPerParty
                + row.ContentMemberType.Value.MeleesPerParty
                + row.ContentMemberType.Value.RangedPerParty;
            var localizedName = isQuestBattle
                ? GetQuestName(questSheet, linkedQuestId)
                : NormalizeName(row.Name.ToString());
            if (string.IsNullOrWhiteSpace(localizedName))
                continue;

            var englishRow = englishSheet?.GetRow(row.RowId) ?? row;
            var englishName = isQuestBattle
                ? GetQuestName(englishQuestSheet, linkedQuestId)
                : NormalizeName(englishRow.Name.ToString());
            if (string.IsNullOrWhiteSpace(englishName))
                englishName = localizedName;

            var contentTypeName = isQuestBattle
                ? "Quest Battle"
                : NormalizeName(contentType!.Value.Name.ToString());
            var category = isQuestBattle
                ? DutyCategory.Solo
                : ClassifyDutyCategory(
                    territoryTypeId: row.TerritoryType.Value.RowId,
                    contentTypeRowId: contentType!.Value.RowId,
                    contentMemberTypeRowId: row.ContentMemberType.Value.RowId,
                    partySize: partySize,
                    contentTypeName: contentTypeName);
            var entry = new DutyCatalogEntry
            {
                ContentFinderConditionId = row.RowId,
                TerritoryTypeId = row.TerritoryType.Value.RowId,
                Name = localizedName,
                EnglishName = englishName,
                ContentTypeName = contentTypeName,
                ExpansionName = GetExpansionName(row.TerritoryType.Value.ExVersion.ValueNullable?.RowId ?? 0),
                SupportNote = DefaultSupportNote,
                LevelRequired = row.ClassJobLevelRequired,
                SortKey = row.SortKey,
                ExVersion = row.TerritoryType.Value.ExVersion.ValueNullable?.RowId ?? 0,
                ContentTypeRowId = contentType?.RowId ?? 0,
                ContentMemberTypeRowId = row.ContentMemberType.Value.RowId,
                PartySize = partySize,
                Category = category,
                SupportLevel = DutySupportLevel.PassiveOnly,
                ClearanceStatus = DutyClearanceStatus.NotCleared,
                IsPlannedTest = false,
                IsMainScenario = false,
            };

            entries.Add(entry);
            entriesByCfc[entry.ContentFinderConditionId] = entry;
            entriesByTerritory[entry.TerritoryTypeId] = entry;
        }

        AddSyntheticTreasureDutyEntries(dataManager);
        ReloadMaturity();

        entries.Sort(static (left, right) =>
        {
            var byExpansion = left.ExVersion.CompareTo(right.ExVersion);
            if (byExpansion != 0)
                return byExpansion;

            var byLevel = left.LevelRequired.CompareTo(right.LevelRequired);
            if (byLevel != 0)
                return byLevel;

            var bySortKey = left.SortKey.CompareTo(right.SortKey);
            if (bySortKey != 0)
                return bySortKey;

            return string.Compare(left.EnglishName, right.EnglishName, StringComparison.OrdinalIgnoreCase);
        });

        var categorySummary = string.Join(
            ", ",
            entries
                .GroupBy(x => x.Category)
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}={x.Count()}"));
        log.Information($"[ADS] Built instanced duty catalog with {entries.Count} rows. Categories: {categorySummary}.");
    }

    public IReadOnlyList<DutyCatalogEntry> Entries
        => entries;

    public string MaturityConfigPath
        => maturityPath;

    public string LastMaturityLoadStatus { get; private set; } = "Duty maturity not loaded yet.";

    public bool ReloadMaturity()
    {
        ResetMaturityDefaults();

        try
        {
            EnsureMaturitySeeded();
            var json = File.ReadAllText(maturityPath);
            var manifest = JsonSerializer.Deserialize<DutyMaturityManifest>(json, JsonOptions) ?? new DutyMaturityManifest();
            manifest.Duties ??= [];
            var applied = 0;

            applied = ApplyMaturityManifest(entries, manifest);

            LastMaturityLoadStatus = $"Loaded duty maturity overlay from {maturityPath}: applied {applied}/{manifest.Duties.Count} row(s).";
            log.Information($"[ADS] {LastMaturityLoadStatus}");
            return true;
        }
        catch (Exception ex)
        {
            LastMaturityLoadStatus = $"Failed to load duty maturity overlay from {maturityPath}: {ex.Message}. Using default untested maturity for all catalog rows.";
            log.Warning(ex, $"[ADS] {LastMaturityLoadStatus}");
            return false;
        }
    }

    public bool SaveMaturityOverrides()
        => SaveMaturityOverridesFromRows(entries, applySavedRows: false);

    public bool SaveMaturityOverrides(IReadOnlyList<DutyMaturityDraftRow> draftRows)
        => SaveMaturityOverridesFromRows(draftRows, applySavedRows: true);

    private bool SaveMaturityOverridesFromRows(IEnumerable<IDutyMaturityCatalogRow> sourceRows, bool applySavedRows)
    {
        try
        {
            var directory = Path.GetDirectoryName(maturityPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var sourceList = sourceRows.ToList();
            var manifest = BuildMaturityManifest(sourceList);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(maturityPath, json + Environment.NewLine);
            if (applySavedRows)
                ApplyMaturityRowsToLiveEntries(sourceList);

            LastMaturityLoadStatus = $"Saved duty maturity overlay to {maturityPath}: {manifest.Duties.Count} override row(s).";
            log.Information($"[ADS] {LastMaturityLoadStatus}");
            return true;
        }
        catch (Exception ex)
        {
            LastMaturityLoadStatus = $"Failed to save duty maturity overlay to {maturityPath}: {ex.Message}.";
            log.Warning(ex, $"[ADS] {LastMaturityLoadStatus}");
            return false;
        }
    }

    public DutyCatalogEntry? ResolveCurrentDuty(uint contentFinderConditionId, uint territoryTypeId)
    {
        if (contentFinderConditionId != 0 && entriesByCfc.TryGetValue(contentFinderConditionId, out var byCfc))
            return byCfc;

        if (territoryTypeId != 0 && entriesByTerritory.TryGetValue(territoryTypeId, out var byTerritory))
            return byTerritory;

        return null;
    }

    private void ResetMaturityDefaults()
    {
        foreach (var entry in entries)
        {
            entry.ClearanceStatus = DutyClearanceStatus.NotCleared;
            entry.SupportLevel = DutySupportLevel.PassiveOnly;
            entry.IsPlannedTest = false;
            entry.IsMainScenario = false;
            entry.SupportNote = DefaultSupportNote;
        }
    }

    private void EnsureMaturitySeeded()
    {
        if (File.Exists(maturityPath))
            return;

        File.WriteAllText(maturityPath, GetDefaultMaturityJson());
        File.SetLastWriteTimeUtc(maturityPath, DateTime.UtcNow - TimeSpan.FromDays(2));
        LastMaturityLoadStatus = "Duty maturity config was missing, so ADS seeded a built-in empty fallback until the botologyupdates cache refresh succeeds.";
        log.Warning($"[ADS] {LastMaturityLoadStatus}");
    }

    internal static DutyMaturityManifest BuildMaturityManifest(IEnumerable<IDutyMaturityCatalogRow> sourceEntries)
        => new()
        {
            SchemaVersion = 1,
            Description = "Human-edited ADS duty maturity overlay. Rows absent from this file use built-in untested defaults.",
            Duties = sourceEntries
                .Where(entry => !IsDefaultMaturityEntry(entry))
                .Select(CreateMaturityRow)
                .ToList(),
        };

    internal static int ApplyMaturityManifest(IReadOnlyList<DutyCatalogEntry> targetEntries, DutyMaturityManifest manifest)
    {
        manifest.Duties ??= [];
        var applied = 0;
        foreach (var row in manifest.Duties)
        {
            if (!TryFindMaturityEntry(targetEntries, row, out var entry))
                continue;

            entry.ClearanceStatus = row.ClearanceStatus;
            entry.SupportLevel = row.SupportLevel;
            entry.IsPlannedTest = row.IsPlannedTest;
            entry.IsMainScenario = row.IsMainScenario;
            entry.SupportNote = string.IsNullOrWhiteSpace(row.SupportNote)
                ? DefaultSupportNote
                : DutyMaturityCatalog.NormalizeText(row.SupportNote);
            applied++;
        }

        return applied;
    }

    internal static bool IsDefaultMaturityEntry(IDutyMaturityCatalogRow entry)
        => DutyMaturityCatalog.IsDefaultMaturityEntry(entry);

    private static DutyMaturityRow CreateMaturityRow(IDutyMaturityCatalogRow entry)
        => new()
        {
            ContentFinderConditionId = entry.ContentFinderConditionId,
            TerritoryTypeId = entry.TerritoryTypeId,
            DutyEnglishName = entry.EnglishName,
            ClearanceStatus = entry.ClearanceStatus,
            SupportLevel = entry.SupportLevel,
            IsPlannedTest = entry.IsPlannedTest,
            IsMainScenario = entry.IsMainScenario,
            SupportNote = DutyMaturityCatalog.IsDefaultSupportNote(entry.SupportNote)
                ? string.Empty
                : entry.SupportNote.Trim(),
        };

    private void ApplyMaturityRowsToLiveEntries(IReadOnlyList<IDutyMaturityCatalogRow> sourceRows)
    {
        foreach (var sourceRow in sourceRows)
        {
            if (!TryFindMaturityEntry(entries, CreateMaturityRow(sourceRow), out var entry))
                continue;

            entry.ClearanceStatus = sourceRow.ClearanceStatus;
            entry.SupportLevel = sourceRow.SupportLevel;
            entry.IsPlannedTest = sourceRow.IsPlannedTest;
            entry.IsMainScenario = sourceRow.IsMainScenario;
            entry.SupportNote = string.IsNullOrWhiteSpace(sourceRow.SupportNote)
                ? DefaultSupportNote
                : DutyMaturityCatalog.NormalizeText(sourceRow.SupportNote);
        }
    }

    private static bool TryFindMaturityEntry(IReadOnlyList<DutyCatalogEntry> sourceEntries, DutyMaturityRow row, out DutyCatalogEntry entry)
    {
        if (row.ContentFinderConditionId != 0)
        {
            entry = sourceEntries.FirstOrDefault(x => x.ContentFinderConditionId == row.ContentFinderConditionId)!;
            if (entry is not null)
                return true;
        }

        if (row.TerritoryTypeId != 0)
        {
            entry = sourceEntries.FirstOrDefault(x => x.TerritoryTypeId == row.TerritoryTypeId)!;
            if (entry is not null)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(row.DutyEnglishName))
        {
            entry = sourceEntries.FirstOrDefault(x => x.EnglishName.Equals(row.DutyEnglishName, StringComparison.OrdinalIgnoreCase))!;
            return entry is not null;
        }

        entry = null!;
        return false;
    }

    private void AddSyntheticTreasureDutyEntries(IDataManager dataManager)
    {
        var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
        if (territorySheet is null)
            return;

        foreach (var territoryId in TreasureDungeonData.GetSupportedDutyTerritories())
        {
            if (entriesByTerritory.ContainsKey(territoryId))
                continue;

            if (!territorySheet.TryGetRow(territoryId, out var territory))
                continue;

            var territoryName = NormalizeName(territory.PlaceName.Value.Name.ToString());
            if (string.IsNullOrWhiteSpace(territoryName))
                territoryName = $"Treasure Duty {territoryId}";

            var entry = new DutyCatalogEntry
            {
                ContentFinderConditionId = 0,
                TerritoryTypeId = territoryId,
                Name = territoryName,
                EnglishName = territoryName,
                ContentTypeName = "Treasure Dungeon",
                ExpansionName = GetExpansionName(territory.ExVersion.ValueNullable?.RowId ?? 0),
                SupportNote = DefaultSupportNote,
                LevelRequired = 0,
                SortKey = ushort.MaxValue,
                ExVersion = territory.ExVersion.ValueNullable?.RowId ?? 0,
                ContentTypeRowId = 0,
                ContentMemberTypeRowId = 0,
                PartySize = 4,
                Category = DutyCategory.TreasureDungeon,
                SupportLevel = DutySupportLevel.PassiveOnly,
                ClearanceStatus = DutyClearanceStatus.NotCleared,
                IsPlannedTest = false,
                IsMainScenario = false,
            };

            entries.Add(entry);
            entriesByTerritory[territoryId] = entry;
        }
    }

    private static string NormalizeName(string name)
        => DutyMaturityCatalog.NormalizeText(name);

    private static bool TryGetLinkedQuestId(ContentFinderCondition row, out uint questId)
    {
        questId = 0;
        if (!row.Content.TryGetValue<QuestBattle>(out var questBattle)
            || !questBattle.Quest.Is<Quest>())
        {
            return false;
        }

        questId = questBattle.Quest.RowId;
        return questId != 0;
    }

    private static string GetQuestName(ExcelSheet<Quest>? questSheet, uint questId)
        => questSheet is not null && questSheet.TryGetRow(questId, out var quest)
            ? NormalizeName(quest.Name.ToString())
            : string.Empty;

    private static DutyCategory ClassifyDutyCategory(
        uint territoryTypeId,
        uint contentTypeRowId,
        uint contentMemberTypeRowId,
        int partySize,
        string contentTypeName)
    {
        if (TreasureDungeonData.IsSupportedDutyTerritory(territoryTypeId))
            return DutyCategory.TreasureDungeon;

        var normalizedType = NormalizeName(contentTypeName).ToLowerInvariant();
        if (normalizedType.Contains("guild hest", StringComparison.Ordinal)
            || normalizedType.Contains("guildhest", StringComparison.Ordinal))
        {
            return DutyCategory.GuildHest;
        }

        if (normalizedType.Contains("deep dungeon", StringComparison.Ordinal))
            return DutyCategory.DeepDungeon;

        if (normalizedType.Contains("treasure", StringComparison.Ordinal))
            return DutyCategory.TreasureDungeon;

        if (normalizedType.Contains("alliance", StringComparison.Ordinal) || partySize >= 24)
            return DutyCategory.AllianceRaid;

        if (partySize <= 1)
            return DutyCategory.Solo;

        if (partySize == 4)
            return DutyCategory.FourMan;

        if (partySize == 8)
            return DutyCategory.EightMan;

        return contentTypeRowId switch
        {
            5 => DutyCategory.GuildHest,
            21 => DutyCategory.DeepDungeon,
            _ => contentMemberTypeRowId switch
            {
                3 => DutyCategory.Solo,
                4 => DutyCategory.FourMan,
                5 => DutyCategory.EightMan,
                6 => DutyCategory.AllianceRaid,
                _ => DutyCategory.Other,
            },
        };
    }

    private static string GetExpansionName(uint exVersion)
        => exVersion switch
        {
            0 => "ARR",
            1 => "HW",
            2 => "SB",
            3 => "ShB",
            4 => "EW",
            5 => "DT",
            _ => $"EX{exVersion}",
        };

    private static string GetDefaultMaturityJson()
        => """
{
  "schemaVersion": 1,
  "description": "Minimal built-in ADS duty maturity fallback. The live DEFAULT cache should normally be refreshed from botologyupdates.",
  "duties": []
}
""";
}
