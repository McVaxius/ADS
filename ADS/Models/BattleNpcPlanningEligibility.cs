namespace ADS.Models;

internal sealed record BattleNpcPlanningEligibility(
    ObservedMonster Monster,
    float? Distance,
    float? VerticalDelta,
    ObjectPriorityRule? EffectiveRule,
    InteractableClass? EffectiveClassification,
    bool PassesVerticalSanity,
    bool SuppressedByRuleGates)
{
    public bool IsEligibleBlocker
        => PassesVerticalSanity && !SuppressedByRuleGates;
}
