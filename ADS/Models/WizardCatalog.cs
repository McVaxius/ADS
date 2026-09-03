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
            "Understand inherited presets, combined context views, complete-context saves, and local PR preparation.",
            [
                Page("overview", "Presets, Contexts & Inheritance", "Object Rules shows one combined effective view, not one backing JSON file. DEFAULT owns indexed Global and territory shards; a custom preset inherits every DEFAULT context for which it has no override.",
                    ["Choose a custom preset for ordinary authoring; DEFAULT writes remain debug-protected.", "Check one or more contexts to filter the combined table and operate on those backing shards.", "The context state label tells you whether rows come from DEFAULT, an override, a custom-only file, an empty override, or no file yet."],
                    ["/ads rules"]),
                Page("safety", "Complete Saves, Empty Overrides & Revert", "Saving a changed context writes its complete replacement, including rows hidden by text or row filters. An empty custom shard intentionally suppresses every inherited row in that context.",
                    ["Use one checked context when creating the first row for an empty or no-file territory.", "Prefer disabled rows when you want reviewable intent; confirm explicitly before saving an empty override.", "Revert deletes each checked saved override. A custom-only context then disappears because DEFAULT has nothing to inherit."],
                    ["/ads rules"]),
                Page("steps", "Checkout & Multi-context Promotion", "Configure and validate a local BotologyUpdates checkout in Object Rules or Settings > Data & Rules, then promote every eligible checked saved override in one batch.",
                    ["Keep the draft clean and resolve any disk conflict before promotion.", "Review eligible and skipped context counts; inherited and no-file selections are skipped.", "ADS copies complete saved shards and updates index.json once for new contexts. It never stages, commits, pushes, switches branches, or opens a pull request; GitHub submission stays manual."],
                    ["/ads config", "/ads rules"]),
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
