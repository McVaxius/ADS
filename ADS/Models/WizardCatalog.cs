namespace ADS.Models;

public sealed record WizardPage(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Commands);

public sealed record WizardDefinition(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<WizardPage> Pages);

public static class WizardCatalog
{
    public const string DutyOperationsId = "duty-operations";
    public const string RulesDataId = "rules-data";
    public const string UtilitiesId = "utilities";
    public const string TreasureFollowId = "treasure-follow";
    public const string DiagnosticsRecoveryId = "diagnostics-recovery";

    public static IReadOnlyList<WizardDefinition> All { get; } =
    [
        new(
            DutyOperationsId,
            "Duty Operations",
            "Enable ADS, choose outside/inside/resume ownership, and recover safely with Stop or Leave.",
            [
                Page("overview", "Overview", "ADS observes by default. Duty operations become active only after you explicitly grant ownership.",
                    ["Use Start Outside before queueing.", "Use Start Inside after stable instanced-duty truth appears.", "Use Resume after a reload or intentional stop."],
                    ["/ads", "/ads mini"]),
                Page("safety", "Prerequisites & Safety", "Stay logged in and wait for zoning, events, and cutscenes to finish before granting ownership.",
                    ["Stop always releases ADS ownership.", "Leave is available only while ADS owns execution.", "Observing mode keeps diagnostics available without movement ownership."],
                    ["/ads stop", "/ads leave"]),
                Page("steps", "Commands & UI Steps", "Open Main and use the persistent action row for the ownership path that matches your current state.",
                    ["Confirm the duty name and live instanced status in Overview.", "Choose Start Outside, Start Inside, or Resume once.", "Keep Main or compact controls visible and use Stop if anything looks wrong."],
                    ["/ads", "/ads mini", "/ads stop"]),
            ]),
        new(
            RulesDataId,
            "Rules & Data",
            "Refresh the remote cache, work from DEFAULT runtime data, and author object, dialog, and maturity rules.",
            [
                Page("overview", "Overview", "ADS combines the Lumina duty catalog with operator-maintained DEFAULT object, dialog, and maturity data.",
                    ["Remote cache updates are explicit and reviewable.", "DEFAULT is the live runtime dataset.", "Parked presets are safe drafts until loaded into DEFAULT."],
                    ["/ads config"]),
                Page("safety", "Prerequisites & Safety", "Rule edits can change planning and dialog behavior, so export or park a preset before broad edits.",
                    ["Use exact names, kinds, layers, and coordinate gates.", "Review required fields before saving.", "Treat maturity labels as evidence, not execution authority."],
                    []),
                Page("steps", "Commands & UI Steps", "Use Settings > Data & Rules and the existing editors to refresh, inspect, edit, and reload data.",
                    ["Update the rules cache.", "Open the object or dialog rules table and edit DEFAULT.", "Open the maturity editor, save, then confirm the load status."],
                    ["/ads config", "/ads rules", "/ads dialogrules"]),
            ]),
        new(
            UtilitiesId,
            "Utilities",
            "Understand exclusivity, dependencies, repair, extract, desynthesis, and shop-spending safeguards.",
            [
                Page("overview", "Overview", "Utility automation is operator-started and mutually exclusive so only one spending or inventory workflow runs at a time.",
                    ["Repair may use self-repair or an NPC route.", "Extract and desynthesis validate live inventory state.", "Shop purchasing resolves a specific offer and exact additional quantity."],
                    ["/ads repair", "/ads extract", "/ads desynth"]),
                Page("safety", "Prerequisites & Safety", "Dependencies, inventory capacity, currency balances, and live addon identity must be proven before a utility callback.",
                    ["Cancel stops the active utility.", "Shop purchases never retry a submitted callback.", "Ambiguous offers, currency, rows, or confirmations fail closed."],
                    ["/ads cancel"]),
                Page("steps", "Commands & UI Steps", "Open Main > Tools > Treasure And Operations for launchers and status, then start only the intended utility.",
                    ["Check inventory capacity and currency first.", "Start one utility and watch its status.", "Cancel on any unexpected addon, route, or balance change."],
                    ["/ads repair", "/ads extract", "/ads desynth", "/ads shop <itemID> <quantity>", "/ads cancel"]),
            ]),
        new(
            TreasureFollowId,
            "Treasure & Follow",
            "Set leader/follower expectations for BMRAI/VBM, coffers, doors, and Higher/Lower behavior.",
            [
                Page("overview", "Overview", "Treasure duties divide ADS behavior between map opener/leader and follower roles while preserving explicit duty ownership.",
                    ["BMRAI/VBM can own follower movement when ADS has accepted that provider state.", "Coffers remain optional planner targets.", "Door routing and Higher/Lower have separate recovery and automation gates."],
                    ["/ads treasure", "/ads higherlower"]),
                Page("safety", "Prerequisites & Safety", "Confirm the inferred role and portal opener before relying on follow automation.",
                    ["Follower mode never grants arbitrary ownership.", "Door-frame recovery is bounded and can be disabled.", "Higher/Lower diagnostics and automation remain separately configurable."],
                    ["/ads stop"]),
                Page("steps", "Commands & UI Steps", "Use Main > Diagnostics for role/follow truth and the treasure/Higher-Lower tools for route and event detail.",
                    ["Confirm leader or follower role.", "Verify BMRAI/VBM follow status and opener age.", "Review coffer, door, and Higher/Lower settings before starting the duty."],
                    ["/ads", "/ads treasure", "/ads higherlower"]),
            ]),
        new(
            DiagnosticsRecoveryId,
            "Diagnostics & Recovery",
            "Use DTR, status JSON, diagnostics, camera recovery, Stop, and /ads leave to understand and recover a run.",
            [
                Page("overview", "Overview", "ADS exposes operator truth in Main, compact controls, DTR, Status JSON, Analysis JSON, and specialist diagnostics.",
                    ["DTR summarizes ownership and phase.", "Status JSON is stable operator/API evidence.", "Analysis JSON contains deeper planner and observation detail."],
                    ["/ads status", "/ads analysis"]),
                Page("safety", "Prerequisites & Safety", "Recovery actions stay ownership-aware and avoid unsafe transitions, cutscenes, and event state.",
                    ["Camera recovery runs only in ADS-owned duties.", "Stop releases ownership immediately.", "Solo-duty guidance reminds you that /ads leave is available if progress stalls."],
                    ["/ads stop", "/ads leave"]),
                Page("steps", "Commands & UI Steps", "Keep a compact status surface visible and capture JSON before changing state when diagnosing a repeatable problem.",
                    ["Copy Status and Analysis JSON from Main > Diagnostics.", "Use Stop if ADS should immediately release control.", "Use Leave only when ADS owns the duty and exit is intended."],
                    ["/ads mini", "/ads status", "/ads analysis", "/ads stop", "/ads leave"]),
            ]),
    ];

    public static bool IsCompleted(Configuration configuration, string wizardId)
        => wizardId switch
        {
            DutyOperationsId => configuration.DutyOperationsWizardCompleted,
            RulesDataId => configuration.RulesDataWizardCompleted,
            UtilitiesId => configuration.UtilitiesWizardCompleted,
            TreasureFollowId => configuration.TreasureFollowWizardCompleted,
            DiagnosticsRecoveryId => configuration.DiagnosticsRecoveryWizardCompleted,
            _ => false,
        };

    public static void SetCompleted(Configuration configuration, string wizardId, bool completed = true)
    {
        switch (wizardId)
        {
            case DutyOperationsId:
                configuration.DutyOperationsWizardCompleted = completed;
                break;
            case RulesDataId:
                configuration.RulesDataWizardCompleted = completed;
                break;
            case UtilitiesId:
                configuration.UtilitiesWizardCompleted = completed;
                break;
            case TreasureFollowId:
                configuration.TreasureFollowWizardCompleted = completed;
                break;
            case DiagnosticsRecoveryId:
                configuration.DiagnosticsRecoveryWizardCompleted = completed;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(wizardId), wizardId, "Unknown setup wizard.");
        }
    }

    public static bool ShouldAutoOpen(bool loadedExistingConfiguration, Configuration configuration)
        => !loadedExistingConfiguration && !configuration.WizardHubSeen;

    private static WizardPage Page(
        string id,
        string title,
        string body,
        IReadOnlyList<string> steps,
        IReadOnlyList<string> commands)
        => new(id, title, body, steps, commands);
}
