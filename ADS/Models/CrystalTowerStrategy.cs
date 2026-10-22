namespace ADS.Models;

public enum CrystalTowerStrategyMode { None, Sync, Unsync, Invalid }

public static class CrystalTowerStrategy
{
    public static CrystalTowerStrategyMode Parse(string? value)
        => string.IsNullOrWhiteSpace(value) ? CrystalTowerStrategyMode.None
            : string.Equals(value.Trim(), "SYNC", StringComparison.OrdinalIgnoreCase) ? CrystalTowerStrategyMode.Sync
            : string.Equals(value.Trim(), "UNSYNC", StringComparison.OrdinalIgnoreCase) ? CrystalTowerStrategyMode.Unsync
            : CrystalTowerStrategyMode.Invalid;

    public static bool MatchesDuty(uint territoryId, uint cfcId)
        => (territoryId, cfcId) is (174, 92) or (372, 102) or (151, 111);

    public static bool IsValidRule(ObjectPriorityRule rule)
    {
        var mode = Parse(rule.CrystalTowerStrategy);
        return mode == CrystalTowerStrategyMode.None
            || mode != CrystalTowerStrategyMode.Invalid && MatchesDuty(rule.TerritoryTypeId, rule.ContentFinderConditionId);
    }

    // Strategy travels with copied/exported rule data, independent of a preset's editable name.
    public static CrystalTowerStrategyMode Resolve(IEnumerable<ObjectPriorityRule> rules, DutyContextSnapshot context)
    {
        if (!MatchesDuty(context.TerritoryTypeId, context.ContentFinderConditionId))
            return CrystalTowerStrategyMode.None;
        var modes = rules.Where(rule => rule.Enabled
                && rule.TerritoryTypeId == context.TerritoryTypeId
                && rule.ContentFinderConditionId == context.ContentFinderConditionId)
            .Select(rule => Parse(rule.CrystalTowerStrategy))
            .Where(mode => mode != CrystalTowerStrategyMode.None)
            .Distinct().ToArray();
        return modes.Length == 0 ? CrystalTowerStrategyMode.None
            : modes.Length == 1 ? modes[0] : CrystalTowerStrategyMode.Invalid;
    }
}
