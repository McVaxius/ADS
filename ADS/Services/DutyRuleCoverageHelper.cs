using ADS.Models;

namespace ADS.Services;

internal static class DutyRuleCoverageHelper
{
    public static Dictionary<string, int> BuildExplicitRuleCountsByDuty(
        IReadOnlyList<DutyCatalogEntry> entries,
        ObjectPriorityRuleService objectPriorityRuleService)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
            counts[DutyMaturityCatalog.BuildDutyCatalogKey(entry)] = CountExplicitRulesForDuty(entry, objectPriorityRuleService);

        return counts;
    }

    public static int CountExplicitRulesForDuty(
        DutyCatalogEntry entry,
        ObjectPriorityRuleService objectPriorityRuleService)
    {
        var context = new DutyContextSnapshot
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = true,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInQuestEvent = false,
            OccupiedInEvent = false,
            OccupiedInCutSceneEvent = false,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = entry.TerritoryTypeId,
            MapId = 0,
            ContentFinderConditionId = entry.ContentFinderConditionId,
            CurrentDuty = entry,
        };

        return objectPriorityRuleService.Current.Rules.Count(rule =>
            IsExplicitDutyRule(rule)
            && objectPriorityRuleService.MatchesCurrentDutyScopeForEditor(rule, context));
    }

    public static bool IsGlobalRule(ObjectPriorityRule rule)
        => !IsExplicitDutyRule(rule);

    public static bool IsExplicitDutyRule(ObjectPriorityRule rule)
        => rule.ContentFinderConditionId != 0
           || rule.TerritoryTypeId != 0
           || !string.IsNullOrWhiteSpace(rule.DutyEnglishName);

    public static string GetRuleCategoryLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.Classification) ? "(none)" : rule.Classification;
}
