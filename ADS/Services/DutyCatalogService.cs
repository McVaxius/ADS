using ADS.Models;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DutyCatalogService
{
    private static readonly HashSet<string> PilotDutyNames =
    [
        "the tam-tara deepcroft",
        "the thousand maws of toto-rak",
        "brayflox's longstop",
        "the stone vigil",
        "the aurum vale",
        "castrum meridianum",
    ];

    private static readonly HashSet<string> LaterWaveDutyNames =
    [
        "sastasha",
        "copperbell mines",
        "haukke manor",
        "the keeper of the lake",
        "the praetorium",
    ];

    private static readonly Dictionary<string, DutyClearanceStatus> ClearanceStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sastasha"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["copperbell mines"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["haukke manor"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["halatali"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the tam-tara deepcroft"] = DutyClearanceStatus.OnePlayerDutySupport,
        ["the thousand maws of toto-rak"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the keeper of the lake"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the stone vigil"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the aurum vale"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["hells' lid"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the sunken temple of qarn"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["castrum meridianum"] = DutyClearanceStatus.FourPlayerSyncCleared,
        ["the praetorium"] = DutyClearanceStatus.FourPlayerSyncCleared,
        ["dzemael darkhold"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["the burn"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["cutter's cry"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["pharos sirius"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["hullbreaker isle"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["doma castle"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["castrum abania"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
        ["brayflox's longstop"] = DutyClearanceStatus.OnePlayerUnsyncCleared,
    };

    private readonly List<DutyCatalogEntry> entries = [];
    private readonly Dictionary<uint, DutyCatalogEntry> entriesByCfc = [];
    private readonly Dictionary<uint, DutyCatalogEntry> entriesByTerritory = [];

    public DutyCatalogService(IDataManager dataManager, IPluginLog log)
    {
        var contentFinderSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        var englishSheet = dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
        if (contentFinderSheet is null)
        {
            log.Warning("[ADS] ContentFinderCondition sheet was unavailable; ADS duty catalog is empty.");
            return;
        }

        foreach (var row in contentFinderSheet)
        {
            if (row.ContentType.ValueNullable is null || row.ContentType.Value.RowId != 2)
                continue;

            if (row.TerritoryType.ValueNullable is null || row.ContentMemberType.ValueNullable is null)
                continue;

            var partySize = row.ContentMemberType.Value.TanksPerParty
                + row.ContentMemberType.Value.HealersPerParty
                + row.ContentMemberType.Value.MeleesPerParty
                + row.ContentMemberType.Value.RangedPerParty;
            if (partySize != 4)
                continue;

            var localizedName = NormalizeName(row.Name.ToString());
            if (string.IsNullOrWhiteSpace(localizedName))
                continue;

            var englishRow = englishSheet?.GetRow(row.RowId) ?? row;
            var englishName = NormalizeName(englishRow.Name.ToString());
            if (string.IsNullOrWhiteSpace(englishName))
                englishName = localizedName;

            var supportLevel = DutySupportLevel.PassiveOnly;
            var supportNote = "Passive observation and owned FSM testing are allowed; pilot execution still needs validation.";
            if (PilotDutyNames.Contains(englishName.ToLowerInvariant()))
            {
                supportLevel = DutySupportLevel.ActiveSupported;
                supportNote = "Pilot active wave: simple ARR first.";
            }
            else if (LaterWaveDutyNames.Contains(englishName.ToLowerInvariant()))
            {
                supportNote = "Planned test list; passive observation and owned FSM testing are allowed before pilot promotion.";
            }

            var entry = new DutyCatalogEntry
            {
                ContentFinderConditionId = row.RowId,
                TerritoryTypeId = row.TerritoryType.Value.RowId,
                Name = localizedName,
                EnglishName = englishName,
                ContentTypeName = NormalizeName(row.ContentType.Value.Name.ToString()),
                ExpansionName = GetExpansionName(row.TerritoryType.Value.ExVersion.ValueNullable?.RowId ?? 0),
                SupportNote = supportNote,
                LevelRequired = row.ClassJobLevelRequired,
                SortKey = row.SortKey,
                ExVersion = row.TerritoryType.Value.ExVersion.ValueNullable?.RowId ?? 0,
                ContentTypeRowId = row.ContentType.Value.RowId,
                ContentMemberTypeRowId = row.ContentMemberType.Value.RowId,
                PartySize = partySize,
                SupportLevel = supportLevel,
                ClearanceStatus = ClearanceStatuses.GetValueOrDefault(englishName, DutyClearanceStatus.NotCleared),
                IsPlannedTest = LaterWaveDutyNames.Contains(englishName.ToLowerInvariant()),
            };

            entries.Add(entry);
            entriesByCfc[entry.ContentFinderConditionId] = entry;
            entriesByTerritory[entry.TerritoryTypeId] = entry;
        }

        AddSyntheticTreasureDutyEntries(dataManager);

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

        log.Information($"[ADS] Built 4-man duty catalog with {entries.Count} rows, {entries.Count(x => x.SupportsPassiveObservation)} supported observation duties, and {entries.Count(x => x.SupportsActiveExecution)} active pilot duties.");
    }

    public IReadOnlyList<DutyCatalogEntry> Entries
        => entries;

    public DutyCatalogEntry? ResolveCurrentDuty(uint contentFinderConditionId, uint territoryTypeId)
    {
        if (contentFinderConditionId != 0 && entriesByCfc.TryGetValue(contentFinderConditionId, out var byCfc))
            return byCfc;

        if (territoryTypeId != 0 && entriesByTerritory.TryGetValue(territoryTypeId, out var byTerritory))
            return byTerritory;

        return null;
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
                SupportNote = "Synthetic treasure-duty catalog row from built-in treasure routing/classification data.",
                LevelRequired = 0,
                SortKey = ushort.MaxValue,
                ExVersion = territory.ExVersion.ValueNullable?.RowId ?? 0,
                ContentTypeRowId = 0,
                ContentMemberTypeRowId = 0,
                PartySize = 4,
                SupportLevel = DutySupportLevel.ActiveSupported,
                ClearanceStatus = DutyClearanceStatus.FourPlayerSyncCleared,
                IsPlannedTest = false,
            };

            entries.Add(entry);
            entriesByTerritory[territoryId] = entry;
        }
    }

    private static string NormalizeName(string name)
        => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
}
