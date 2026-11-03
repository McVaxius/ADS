using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

// A read-only catalog query: usable before entry and independent of the active duty.
public static class DutyMaturityIpc
{
    public const string Channel = "ADS.Duty.GetMaturityJson";

    public static string Query(IEnumerable<DutyCatalogEntry> catalog, uint territoryTypeId, uint contentFinderConditionId)
    {
        var matches = territoryTypeId == 0 || contentFinderConditionId == 0
            ? []
            : catalog.Where(entry => entry.TerritoryTypeId == territoryTypeId &&
                entry.ContentFinderConditionId == contentFinderConditionId).Take(2).ToArray();
        var entry = matches.Length == 1 ? matches[0] : null;
        var success = entry != null && Enum.IsDefined(entry.ClearanceStatus);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            success,
            territoryTypeId,
            contentFinderConditionId,
            dutyName = entry?.Name ?? string.Empty,
            clearanceStatus = success ? entry!.ClearanceStatus.ToString() : string.Empty,
            clearanceLevel = success ? (int?)entry!.ClearanceStatus : null,
            supportLevel = entry?.SupportLevel.ToString() ?? string.Empty,
            eligibleForQuest = success && entry!.ClearanceStatus != DutyClearanceStatus.NotCleared,
            message = success ? string.Empty : "ADS has no unique, valid maturity row for the requested territory/CFC.",
        });
    }
}
