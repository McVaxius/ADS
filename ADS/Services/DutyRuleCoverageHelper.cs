using System.Globalization;
using ADS.Models;

namespace ADS.Services;

internal readonly record struct DutyRuleIdentity(uint ContentFinderConditionId, uint TerritoryTypeId, string NormalizedEnglishName)
{
    public static DutyRuleIdentity From(DutyCatalogEntry entry)
        => new(entry.ContentFinderConditionId, entry.TerritoryTypeId, DutyRuleCoverageHelper.NormalizeDutyLookupName(entry.EnglishName));
}

internal readonly record struct DutyRuleCoverage(
    int AssociatedRuleCount,
    int EnabledRuleCount,
    int EnabledValidWaypointCount,
    int RedundantScopeMismatchCount)
{
    public DutyRuleCoverage Add(ObjectPriorityRule rule, bool redundantScopeMismatch)
        => new(
            AssociatedRuleCount + 1,
            EnabledRuleCount + (rule.Enabled ? 1 : 0),
            EnabledValidWaypointCount + (rule.Enabled && DutyRuleCoverageHelper.HasValidWaypoint(rule) ? 1 : 0),
            RedundantScopeMismatchCount + (redundantScopeMismatch ? 1 : 0));
}

internal readonly record struct DutyRuleCoverageSnapshot(
    IReadOnlyDictionary<string, DutyRuleCoverage> ByDuty,
    int GlobalRuleCount,
    int UnresolvedRuleCount)
{
    public DutyRuleCoverage Get(IDutyMaturityCatalogRow row)
        => ByDuty.GetValueOrDefault(DutyMaturityCatalog.BuildDutyCatalogKey(row));
}

internal static class DutyRuleCoverageHelper
{
    public static DutyRuleCoverageSnapshot BuildSnapshot(
        IReadOnlyList<DutyCatalogEntry> entries,
        ObjectPriorityRuleService objectPriorityRuleService)
        => BuildSnapshot(entries, objectPriorityRuleService.Current.Rules);

    internal static DutyRuleCoverageSnapshot BuildSnapshot(
        IReadOnlyList<DutyCatalogEntry> entries,
        IReadOnlyList<ObjectPriorityRule> rules)
    {
        var coverage = entries.ToDictionary(
            DutyMaturityCatalog.BuildDutyCatalogKey,
            _ => default(DutyRuleCoverage),
            StringComparer.Ordinal);
        var byCfc = BuildUniqueIndex(entries, entry => entry.ContentFinderConditionId, value => value != 0);
        var byTerritory = BuildUniqueIndex(entries, entry => entry.TerritoryTypeId, value => value != 0);
        var byName = BuildUniqueIndex(
            entries,
            entry => NormalizeDutyLookupName(entry.EnglishName),
            value => !string.IsNullOrWhiteSpace(value),
            StringComparer.OrdinalIgnoreCase);

        var globalCount = 0;
        var unresolvedCount = 0;
        foreach (var rule in rules)
        {
            if (!IsExplicitDutyRule(rule))
            {
                globalCount++;
                continue;
            }

            var entry = ResolveDiagnosticDuty(rule, byCfc, byTerritory, byName);
            if (entry is null)
            {
                unresolvedCount++;
                continue;
            }

            var key = DutyMaturityCatalog.BuildDutyCatalogKey(entry);
            coverage[key] = coverage[key].Add(rule, HasRedundantScopeMismatch(rule, entry));
        }

        return new DutyRuleCoverageSnapshot(coverage, globalCount, unresolvedCount);
    }

    public static Dictionary<string, int> BuildExplicitRuleCountsByDuty(
        IReadOnlyList<DutyCatalogEntry> entries,
        ObjectPriorityRuleService objectPriorityRuleService)
        => BuildExplicitRuleCountsByDuty(entries, objectPriorityRuleService.Current.Rules);

    internal static Dictionary<string, int> BuildExplicitRuleCountsByDuty(
        IReadOnlyList<DutyCatalogEntry> entries,
        IReadOnlyList<ObjectPriorityRule> rules)
        => BuildSnapshot(entries, rules).ByDuty.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.AssociatedRuleCount,
            StringComparer.Ordinal);

    public static int CountExplicitRulesForDuty(DutyCatalogEntry entry, ObjectPriorityRuleService objectPriorityRuleService)
        => objectPriorityRuleService.Current.Rules.Count(rule => RuleAssociatesWithDuty(rule, entry));

    public static bool IsGlobalRule(ObjectPriorityRule rule)
        => !IsExplicitDutyRule(rule);

    public static bool IsExplicitDutyRule(ObjectPriorityRule rule)
        => rule.ContentFinderConditionId != 0
           || rule.TerritoryTypeId != 0
           || !string.IsNullOrWhiteSpace(rule.DutyEnglishName);

    public static string GetRuleCategoryLabel(ObjectPriorityRule rule)
        => string.IsNullOrWhiteSpace(rule.Classification) ? "(none)" : rule.Classification;

    internal static bool RuleAssociatesWithDuty(ObjectPriorityRule rule, DutyCatalogEntry entry)
    {
        if (!IsExplicitDutyRule(rule))
            return false;
        if (rule.ContentFinderConditionId != 0)
            return rule.ContentFinderConditionId == entry.ContentFinderConditionId;
        if (rule.TerritoryTypeId != 0)
            return rule.TerritoryTypeId == entry.TerritoryTypeId;
        return string.Equals(
            NormalizeDutyLookupName(rule.DutyEnglishName),
            NormalizeDutyLookupName(entry.EnglishName),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RuleMatchesDutyEntry(ObjectPriorityRule rule, DutyCatalogEntry entry)
    {
        if (!IsExplicitDutyRule(rule) || !string.IsNullOrWhiteSpace(rule.Alliance))
            return false;
        if (rule.ContentFinderConditionId != 0 && rule.ContentFinderConditionId != entry.ContentFinderConditionId)
            return false;
        if (rule.TerritoryTypeId != 0 && rule.TerritoryTypeId != entry.TerritoryTypeId)
            return false;
        return string.IsNullOrWhiteSpace(rule.DutyEnglishName)
               || string.Equals(
                   NormalizeDutyLookupName(rule.DutyEnglishName),
                   NormalizeDutyLookupName(entry.EnglishName),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRedundantScopeMismatch(ObjectPriorityRule rule, DutyCatalogEntry entry)
        => (rule.ContentFinderConditionId != 0 && rule.ContentFinderConditionId != entry.ContentFinderConditionId)
           || (rule.TerritoryTypeId != 0 && rule.TerritoryTypeId != entry.TerritoryTypeId)
           || (!string.IsNullOrWhiteSpace(rule.DutyEnglishName)
               && !string.Equals(
                   NormalizeDutyLookupName(rule.DutyEnglishName),
                   NormalizeDutyLookupName(entry.EnglishName),
                   StringComparison.OrdinalIgnoreCase));

    internal static bool HasValidWaypoint(ObjectPriorityRule rule)
    {
        var classification = rule.Classification?.Trim() ?? string.Empty;
        if (classification.Equals("XYZ", StringComparison.OrdinalIgnoreCase)
            || classification.Equals("XYZForceMarch", StringComparison.OrdinalIgnoreCase)
            || (rule.DestinationType ?? string.Empty).Equals("XYZ", StringComparison.OrdinalIgnoreCase))
        {
            return HasFiniteCoordinates(rule.WorldCoordinates, 3);
        }

        if (classification.Equals("MapXzDestination", StringComparison.OrdinalIgnoreCase)
            || classification.Equals("MapXzForceMarch", StringComparison.OrdinalIgnoreCase)
            || (rule.DestinationType ?? string.Empty).Equals("MapXZ", StringComparison.OrdinalIgnoreCase))
        {
            return HasFiniteCoordinates(rule.MapCoordinates, 2);
        }

        return false;
    }

    internal static string NormalizeDutyLookupName(string value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? normalized[4..] : normalized;
    }

    private static DutyCatalogEntry? ResolveDiagnosticDuty(
        ObjectPriorityRule rule,
        IReadOnlyDictionary<uint, DutyCatalogEntry?> byCfc,
        IReadOnlyDictionary<uint, DutyCatalogEntry?> byTerritory,
        IReadOnlyDictionary<string, DutyCatalogEntry?> byName)
    {
        if (rule.ContentFinderConditionId != 0)
            return byCfc.GetValueOrDefault(rule.ContentFinderConditionId);
        if (rule.TerritoryTypeId != 0)
            return byTerritory.GetValueOrDefault(rule.TerritoryTypeId);
        return byName.GetValueOrDefault(NormalizeDutyLookupName(rule.DutyEnglishName));
    }

    private static Dictionary<TKey, DutyCatalogEntry?> BuildUniqueIndex<TKey>(
        IEnumerable<DutyCatalogEntry> entries,
        Func<DutyCatalogEntry, TKey> keySelector,
        Func<TKey, bool> include,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var index = new Dictionary<TKey, DutyCatalogEntry?>(comparer);
        foreach (var entry in entries)
        {
            var key = keySelector(entry);
            if (!include(key))
                continue;
            if (!index.TryAdd(key, entry))
                index[key] = null;
        }
        return index;
    }

    private static bool HasFiniteCoordinates(string? value, int expectedCount)
    {
        var parts = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == expectedCount
               && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                                    && double.IsFinite(number));
    }
}
