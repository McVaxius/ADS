using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class HigherLowerAutomationService
{
    private const string AddonName = "TreasureHighLow";
    private const uint TryLuckNodeId = 46;
    private const uint OpenChestNodeId = 47;
    private const string HigherChoice = HigherLowerCardVfxSolverService.HigherChoice;
    private const string LowerChoice = HigherLowerCardVfxSolverService.LowerChoice;
    private const string OpenChestChoice = HigherLowerCardVfxSolverService.OpenChestChoice;
    private const string StartPlayChoice = "StartPlay";
    private const float DirectionInteractRange = 2.0f;
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan PendingDirectionTransitionSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DutyExitQuietGrace = TimeSpan.FromSeconds(15);

    private readonly TreasureHighLowDiagnosticService diagnostics;
    private readonly HigherLowerCardVfxSolverService solver;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime nextStopCommandUtc = DateTime.MinValue;
    private string lastBlockLogKey = string.Empty;
    private string lastMovementHoldLogKey = string.Empty;
    private string lastDecisionLogKey = string.Empty;
    private string lastSessionLogKey = string.Empty;
    private string lastStopLogKey = string.Empty;
    private uint activeDutyKey;
    private int currentGambleStep = 1;
    private int completedPlayCount;
    private AutomationDecision? retainedDecision;
    private AutomationDecision? pendingDirectionDecision;
    private AutomationDecision? lastDecision;
    private string lastBlockedReason = string.Empty;
    private string lastSurfaceSource = "none";
    private string lastDirectionDecisionSource = "none";
    private string lastDirectionTargetName = "none";
    private float? lastDirectionTargetDistance;
    private string pendingDirectionName = string.Empty;
    private ulong pendingDirectionGameObjectId;
    private DateTime pendingDirectionInteractUtc = DateTime.MinValue;
    private DateTime lastHigherLowerActivityUtc = DateTime.MinValue;
    private string lastHigherLowerActivitySource = "none";
    private bool higherLowerActivitySessionArmed;

    public HigherLowerAutomationService(
        TreasureHighLowDiagnosticService diagnostics,
        HigherLowerCardVfxSolverService solver,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        Configuration configuration,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.diagnostics = diagnostics;
        this.solver = solver;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.log = log;
    }

    public bool Enabled
        => configuration.HigherLowerAutomationEnabled;

    public bool HoldMovement { get; private set; }

    public bool BlocksDutyExit { get; private set; }

    public DateTime LastHigherLowerActivityUtc
        => lastHigherLowerActivityUtc;

    public string Status { get; private set; } = "Higher/Lower automation disabled.";

    public HigherLowerAutomationDebugState CaptureDebugState()
    {
        var decision = lastDecision;
        var retained = retainedDecision;
        return new HigherLowerAutomationDebugState(
            Enabled: configuration.HigherLowerAutomationEnabled,
            HoldMovement: HoldMovement,
            Status: Status,
            SessionDutyKey: activeDutyKey,
            SessionStep: currentGambleStep,
            Card: decision?.Card,
            Action: decision?.Action.ToString() ?? "None",
            Source: decision?.Source ?? "none",
            DirectionSource: lastDirectionDecisionSource,
            Surface: lastSurfaceSource,
            BlockedReason: lastBlockedReason,
            Retained: retained is not null,
            RetainedStep: retained?.Step,
            RetainedCard: retained?.Card,
            RetainedAction: retained?.Action.ToString() ?? "None",
            RetainedSource: retained?.Source ?? "none",
            DirectionTargetName: lastDirectionTargetName,
            DirectionTargetDistance: lastDirectionTargetDistance,
            PendingDirectionTarget: pendingDirectionDecision is null ? "none" : pendingDirectionName,
            PendingDirectionInteractAgeSeconds: GetPendingDirectionInteractAgeSeconds(DateTime.UtcNow),
            BlocksDutyExit: BlocksDutyExit,
            LastHigherLowerActivityUtc: lastHigherLowerActivityUtc,
            LastHigherLowerActivitySource: lastHigherLowerActivitySource,
            DutyExitGraceRemainingSeconds: GetDutyExitGraceRemainingSeconds(DateTime.UtcNow));
    }

    public void SetEnabled(bool enabled)
    {
        if (configuration.HigherLowerAutomationEnabled == enabled)
        {
            Status = enabled
                ? BuildStatus(diagnostics.CaptureRuntimeState(), "enabled")
                : "Higher/Lower automation disabled.";
            return;
        }

        configuration.HigherLowerAutomationEnabled = enabled;
        configuration.Save();
        if (!enabled)
        {
            HoldMovement = false;
            BlocksDutyExit = false;
            ClearHigherLowerActivitySession();
            ClearDirectionState(clearTarget: true);
            lastMovementHoldLogKey = string.Empty;
            Status = "Higher/Lower automation disabled.";
            log.Information("[ADS][HLAUTO] disabled.");
            return;
        }

        Status = BuildStatus(diagnostics.CaptureRuntimeState(), "enabled");
        log.Information("[ADS][HLAUTO] enabled.");
    }

    public void Update(DutyContextSnapshot context, OwnershipMode ownershipMode, bool pluginEnabled)
    {
        HoldMovement = false;
        BlocksDutyExit = false;
        var now = DateTime.UtcNow;
        var state = diagnostics.CaptureRuntimeState();
        var solverState = solver.CurrentState;
        ResetSessionForContext(context);

        var surface = DetectSurface(state);
        lastSurfaceSource = surface.Source;
        UpdateHigherLowerActivity(now, context, state, solverState, surface);
        BlocksDutyExit = context.InInstancedDuty && IsDutyExitGraceActive(now);
        var currentDecision = TryBuildCurrentDecision(state, solverState, now, out var cardBlockedReason);
        if (currentDecision is not null)
        {
            lastDecision = currentDecision;
            lastBlockedReason = string.Empty;
        }
        RecordDatamineSurface(context, state, solverState, surface, currentDecision, cardBlockedReason);

        var modeAllowed = IsAutomationModeAllowed(ownershipMode, surface.Present)
                          || (ownershipMode == OwnershipMode.Leaving
                              && (pendingDirectionDecision is not null || BlocksDutyExit));
        if (!pluginEnabled || !context.InInstancedDuty || !modeAllowed)
        {
            Status = configuration.HigherLowerAutomationEnabled
                ? BuildStatus(state, "blocked: ADS is not in a Higher/Lower automation mode")
                : "Higher/Lower suggested mode waiting; ADS is not in a Higher/Lower automation mode.";
            return;
        }

        if (context.IsUnsafeTransition)
        {
            Status = BuildStatus(state, "blocked: unsafe transition");
            return;
        }

        if (context.InCombat)
        {
            Status = BuildStatus(state, "blocked: combat");
            return;
        }

        if (!surface.Present)
        {
            if (TryConfirmPendingDirectionTransition(state, "surface-cleared"))
                return;

            if (pendingDirectionDecision is not null)
            {
                if (ShouldHoldMovementForAutomation(ownershipMode))
                    HoldMovement = true;
                TryStopVnav("pending-direction");
                Status = BuildStatus(state, "waiting: pending direction transition");
                return;
            }

            lastMovementHoldLogKey = string.Empty;
            lastBlockedReason = string.Empty;
            lastDirectionDecisionSource = "none";
            ClearDirectionState(clearTarget: true);
            Status = configuration.HigherLowerAutomationEnabled
                ? BuildStatus(state, "waiting")
                : "Higher/Lower suggested mode waiting.";
            return;
        }

        ApplyMovementHold(ownershipMode, surface);

        if (currentDecision is not null)
            LogDecisionIfNeeded(state, currentDecision, surface);

        if (TryConfirmPendingDirectionTransition(state, "target-transition"))
            return;

        if (ownershipMode == OwnershipMode.Observing)
        {
            lastBlockedReason = string.IsNullOrWhiteSpace(cardBlockedReason)
                ? "observing mode is datamine-only"
                : cardBlockedReason;
            Status = currentDecision is not null
                ? BuildStatus(state, "observing; datamine only")
                : BuildBlockedStatus(state, solverState, "blocked: observing datamine only", lastBlockedReason);
            LogBlockOnce(
                $"{activeDutyKey}:{currentGambleStep}:observing:{lastBlockedReason}:{surface.Source}",
                $"hlauto blocked-observing-datamine-only {BuildSessionLogFields()} surface={EscapeToken(surface.Source)} reason='{Escape(lastBlockedReason)}' {BuildAddonLogFields(state)} solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()} solverReason='{Escape(solverState.Reason)}'");
            return;
        }

        if (!configuration.HigherLowerAutomationEnabled)
        {
            TryStopVnav("manual");
            Status = currentDecision is not null
                ? BuildStatus(state, "suggested; manual click required")
                : BuildBlockedStatus(state, solverState, "blocked: no reliable card solve", cardBlockedReason);
            LogBlockOnce(
                $"{activeDutyKey}:{currentGambleStep}:manual:{cardBlockedReason}:{surface.Source}",
                $"hlauto blocked-manual {BuildSessionLogFields()} surface={EscapeToken(surface.Source)} reason='{Escape(cardBlockedReason)}'");
            return;
        }

        if (activeDutyKey == 0)
        {
            Block(state, solverState, surface, "blocked: no reliable treasure session", "not in supported treasure duty");
            return;
        }

        if (state.SelectYesnoVisible && IsHigherLowerPrompt(state.SelectYesnoPrompt))
        {
            var promptDecision = ResolveCurrentDirectionDecision(currentDecision, out var promptDecisionSource);
            if (promptDecision is null || !IsPlayAction(promptDecision.Action))
            {
                lastDirectionDecisionSource = promptDecisionSource;
                Block(
                    state,
                    solverState,
                    surface,
                    "blocked: no retained high/low direction for prompt",
                    "SelectYesno H/L prompt is visible without a retained or live current-step play direction");
                return;
            }

            lastDirectionDecisionSource = promptDecisionSource;
            TryAnswer(state, promptDecision, promptDecisionSource);
            return;
        }

        if (state.TreasureHighLowVisible)
        {
            if (currentDecision is not null)
            {
                if (currentDecision.Action == HigherLowerAction.CashOut)
                {
                    TryClickTreasureHighLowButton(state, OpenChestNodeId, "Open Chest", currentDecision, currentDecision.Reason);
                    return;
                }

                if (IsPlayAction(currentDecision.Action))
                {
                    lastDirectionDecisionSource = "current";
                    if (TryClickTreasureHighLowButton(state, TryLuckNodeId, "Try Luck", currentDecision, currentDecision.Reason))
                        retainedDecision = currentDecision with { Retained = true, ObservedUtc = DateTime.UtcNow };
                    return;
                }
            }

            if (CanStartOpeningPlay(state))
            {
                var openingDecision = BuildOpeningStartDecision(state, now);
                lastDecision = openingDecision;
                LogDecisionIfNeeded(state, openingDecision, surface);
                TryClickTreasureHighLowButton(state, TryLuckNodeId, "Try Luck", openingDecision, openingDecision.Reason);
                return;
            }

            Block(
                state,
                solverState,
                surface,
                "blocked: incomplete TreasureHighLow addon snapshot",
                string.IsNullOrWhiteSpace(cardBlockedReason)
                    ? "TreasureHighLow is visible but no reliable current card was decoded"
                    : cardBlockedReason,
                incompleteAddon: true);
            return;
        }

        if (state.HighTargetable || state.LowTargetable)
        {
            var directionDecision = ResolveCurrentDirectionDecision(currentDecision, out var directionSource);
            lastDirectionDecisionSource = directionSource;
            if (directionDecision is null || !IsPlayAction(directionDecision.Action))
            {
                Block(
                    state,
                    solverState,
                    surface,
                    "blocked: targetable High/Low without decoded direction",
                    "High/Low target is visible but no retained or live current-step play direction is decoded");
                return;
            }

            TrySelectDirection(state, directionDecision, directionSource);
            return;
        }

        if (state.NotificationChallengeVisible)
        {
            Block(
                state,
                solverState,
                surface,
                "blocked: waiting for Higher/Lower addon/card",
                "notification challenge is visible without a reliable H/L card or target");
            return;
        }

        Block(
            state,
            solverState,
            surface,
            "blocked: no actionable Higher/Lower surface",
            "surface disappeared before automation could act");
    }

    private void ResetSessionForContext(DutyContextSnapshot context)
    {
        var dutyKey = BuildTreasureDutyKey(context);
        if (dutyKey == 0)
        {
            if (activeDutyKey != 0)
                ResetSession("left-treasure-instance", 0);
            return;
        }

        if (activeDutyKey == dutyKey)
            return;

        ResetSession(activeDutyKey == 0 ? "entered-treasure-instance" : "duty-key-changed", dutyKey);
    }

    private void ResetSession(string reason, uint nextDutyKey)
    {
        var previousDutyKey = activeDutyKey;
        activeDutyKey = nextDutyKey;
        currentGambleStep = 1;
        completedPlayCount = 0;
        retainedDecision = null;
        ClearDirectionState(clearTarget: true);
        lastDecision = null;
        lastBlockedReason = string.Empty;
        lastDirectionDecisionSource = "none";
        lastDecisionLogKey = string.Empty;
        lastBlockLogKey = string.Empty;
        lastMovementHoldLogKey = string.Empty;
        ClearHigherLowerActivitySession();

        var logKey = $"{previousDutyKey}:{nextDutyKey}:{reason}";
        if (logKey == lastSessionLogKey)
            return;

        lastSessionLogKey = logKey;
        if (previousDutyKey != 0 || nextDutyKey != 0)
            LogAction($"hlauto session-reset reason={EscapeToken(reason)} previousDutyKey={previousDutyKey} dutyKey={nextDutyKey} step={currentGambleStep}");
    }

    private static uint BuildTreasureDutyKey(DutyContextSnapshot context)
    {
        if (!context.InInstancedDuty
            || context.TerritoryTypeId == 0
            || !TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId))
        {
            return 0;
        }

        return context.ContentFinderConditionId != 0
            ? context.ContentFinderConditionId
            : context.TerritoryTypeId;
    }

    private static HigherLowerSurface DetectSurface(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state)
    {
        var parts = new List<string>(4);
        if (state.TreasureHighLowVisible)
            parts.Add(AddonName);
        if (state.SelectYesnoVisible && IsHigherLowerPrompt(state.SelectYesnoPrompt))
            parts.Add("SelectYesno");
        if (state.NotificationChallengeVisible)
            parts.Add("_NotificationChallenge");
        if (state.HighTargetable || state.LowTargetable)
            parts.Add(state.HighTargetable && state.LowTargetable
                ? "HighLowTargetable"
                : state.HighTargetable ? "HighTargetable" : "LowTargetable");

        return parts.Count == 0
            ? new HigherLowerSurface(false, "none")
            : new HigherLowerSurface(true, string.Join("+", parts));
    }

    private void UpdateHigherLowerActivity(
        DateTime now,
        DutyContextSnapshot context,
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface)
    {
        if (!context.InInstancedDuty)
        {
            ClearHigherLowerActivitySession();
            return;
        }

        if (surface.Present)
        {
            MarkHigherLowerActivity(now, surface.Source, armSession: true);
            return;
        }

        if (!IsDutyExitGraceActive(now) && pendingDirectionDecision is null && retainedDecision is null)
            ClearHigherLowerActivitySession();

        if (!higherLowerActivitySessionArmed)
            return;

        if (solverState.Active)
        {
            MarkHigherLowerActivity(now, "solver");
            return;
        }

        var signalUtc = diagnostics.LastHigherLowerSignalUtc;
        if (signalUtc > lastHigherLowerActivityUtc)
            MarkHigherLowerActivity(signalUtc, diagnostics.LastHigherLowerSignalSource);
    }

    private void MarkHigherLowerActivity(DateTime timestampUtc, string source, bool armSession = false)
    {
        if (timestampUtc < lastHigherLowerActivityUtc)
            return;

        if (armSession)
            higherLowerActivitySessionArmed = true;

        lastHigherLowerActivityUtc = timestampUtc;
        lastHigherLowerActivitySource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    private void ClearHigherLowerActivitySession()
    {
        higherLowerActivitySessionArmed = false;
        lastHigherLowerActivityUtc = DateTime.MinValue;
        lastHigherLowerActivitySource = "none";
    }

    private AutomationDecision? TryBuildCurrentDecision(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        DateTime now,
        out string blockedReason)
    {
        blockedReason = string.Empty;
        if (currentGambleStep < 1)
        {
            blockedReason = "session step is not initialized";
            return null;
        }

        if (!TryGetReliableCard(state, solverState, out var card, out var source, out blockedReason))
            return null;

        return BuildPolicyDecision(
            currentGambleStep,
            card,
            source,
            now,
            retained: false,
            isFirstRealDecision: completedPlayCount <= 0);
    }

    private static bool TryGetReliableCard(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        out int card,
        out string source,
        out string blockedReason)
    {
        card = 0;
        source = "none";
        blockedReason = string.Empty;

        if (state.TreasureHighLowVisible && IsBlankAddonCard(state))
        {
            blockedReason = "addon current card is blank";
            return false;
        }

        var hasSolverCard = TryGetTrustedSolverCard(solverState, out var solverCard, out var solverSource);
        var hasVisualCard = state.CardSourceSafe && state.CurrentCard is >= 1 and <= 9;
        if (hasSolverCard
            && hasVisualCard
            && state.CurrentCard.HasValue
            && solverCard != state.CurrentCard.Value)
        {
            blockedReason = string.Create(
                CultureInfo.InvariantCulture,
                $"trusted card mismatch; decodedTextureCard={solverCard}; visualCard={state.CurrentCard.Value}; solverSource='{Escape(solverSource)}'; currentGraphicKey='{Escape(state.CurrentGraphicKey)}'");
            return false;
        }

        int? trustedCard = null;
        var trustedSource = "none";
        if (hasSolverCard)
        {
            trustedCard = solverCard;
            trustedSource = solverSource;
        }
        else if (hasVisualCard && state.CurrentCard.HasValue)
        {
            trustedCard = state.CurrentCard.Value;
            trustedSource = HigherLowerCardVfxSolverService.VisualGraphicKeySource;
        }

        if (trustedCard.HasValue)
        {
            if (state.AddonCurrentCard is >= 1 and <= 9
                && state.AddonCurrentCard.Value != trustedCard.Value)
            {
                blockedReason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"addon-card-mismatch addonCard={state.AddonCurrentCard.Value}; decodedCard={trustedCard.Value}; decodedSource='{Escape(trustedSource)}'; currentGraphicKey='{Escape(state.CurrentGraphicKey)}'");
                return false;
            }

            card = trustedCard.Value;
            source = state.AddonCurrentCard == trustedCard && !trustedSource.Contains("addon-atk-match", StringComparison.OrdinalIgnoreCase)
                ? $"{trustedSource}+addon-atk-match"
                : trustedSource;
            return true;
        }

        if (state.AddonCurrentCard is >= 1 and <= 9)
        {
            blockedReason = string.Create(
                CultureInfo.InvariantCulture,
                $"addon-only card blocked; addonCard={state.AddonCurrentCard.Value}; currentGraphicKey='{Escape(state.CurrentGraphicKey)}'; solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()}; solverReason='{Escape(solverState.Reason)}'");
            return false;
        }

        blockedReason = string.Create(
            CultureInfo.InvariantCulture,
            $"no reliable card; addonCurrentCard={state.AddonCurrentCardText}; solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()}; solverReason='{Escape(solverState.Reason)}'");
        return false;
    }

    private static bool TryGetTrustedSolverCard(
        HigherLowerCardVfxSolverService.SolverState solverState,
        out int card,
        out string source)
    {
        card = 0;
        source = string.IsNullOrWhiteSpace(solverState.CardSource) ? "unknown" : solverState.CardSource;
        if (solverState.Confidence != HigherLowerCardVfxSolverService.SolverConfidence.High
            || !solverState.CurrentCard.HasValue
            || solverState.CurrentCard.Value is < 1 or > 9
            || IsAddonOnlyCardSource(source))
        {
            return false;
        }

        card = solverState.CurrentCard.Value;
        return true;
    }

    private static bool IsAddonOnlyCardSource(string source)
        => string.Equals(source, HigherLowerCardVfxSolverService.AddonAtkValueSource, StringComparison.OrdinalIgnoreCase)
           || source.StartsWith($"{HigherLowerCardVfxSolverService.AddonAtkValueSource}+", StringComparison.OrdinalIgnoreCase);

    private static AutomationDecision BuildPolicyDecision(
        int step,
        int card,
        string source,
        DateTime now,
        bool retained,
        bool isFirstRealDecision)
    {
        var action = ResolvePolicyAction(step, card, isFirstRealDecision);
        var reason = action switch
        {
            HigherLowerAction.PlayHigh when isFirstRealDecision && card == 5 => "first revealed card always plays; card 5 chooses High",
            HigherLowerAction.PlayHigh when isFirstRealDecision => "first revealed card always plays; low card chooses High",
            HigherLowerAction.PlayLow when isFirstRealDecision => "first revealed card always plays; high card chooses Low",
            HigherLowerAction.CashOut when step == 2 => "step 2 cashes out on card 5",
            HigherLowerAction.CashOut when step == 3 => "step 3 cashes out on cards 4,5,6",
            HigherLowerAction.CashOut => "step 4+ cashes out on cards 3,4,5,6,7",
            HigherLowerAction.PlayHigh when step == 1 && card == 5 => "step 1 always plays; card 5 chooses High",
            HigherLowerAction.PlayHigh => "low card chooses High",
            HigherLowerAction.PlayLow => "high card chooses Low",
            _ => "policy decision",
        };

        return new AutomationDecision(
            Step: Math.Max(1, step),
            Card: card,
            Action: action,
            Choice: ChoiceForAction(action),
            Source: string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            Retained: retained,
            ObservedUtc: now,
            Reason: reason);
    }

    private AutomationDecision BuildOpeningStartDecision(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        DateTime now)
        => new(
            Step: currentGambleStep,
            Card: null,
            Action: HigherLowerAction.StartPlay,
            Choice: StartPlayChoice,
            Source: string.IsNullOrWhiteSpace(state.AddonCurrentCardSource) ? HigherLowerCardVfxSolverService.AddonAtkValueSource : state.AddonCurrentCardSource,
            Retained: false,
            ObservedUtc: now,
            Reason: "step 1 starts Higher/Lower; addon current card is blank");

    private static HigherLowerAction ResolvePolicyAction(int step, int card, bool isFirstRealDecision)
    {
        if (isFirstRealDecision || step <= 1)
            return card >= 6 ? HigherLowerAction.PlayLow : HigherLowerAction.PlayHigh;

        if (step == 2 && card == 5)
            return HigherLowerAction.CashOut;

        if (step == 3 && card is >= 4 and <= 6)
            return HigherLowerAction.CashOut;

        if (step >= 4 && card is >= 3 and <= 7)
            return HigherLowerAction.CashOut;

        return card >= 6 ? HigherLowerAction.PlayLow : HigherLowerAction.PlayHigh;
    }

    private void ApplyMovementHold(OwnershipMode ownershipMode, HigherLowerSurface surface)
    {
        if (ShouldHoldMovementForAutomation(ownershipMode))
        {
            HoldMovement = true;
            LogMovementHoldOnce($"hlauto preempt-progression surface={EscapeToken(surface.Source)} {BuildSessionLogFields()}");
            return;
        }

        if (ownershipMode == OwnershipMode.Observing)
            LogMovementHoldOnce($"hlauto movement-hold mode=observing action=skip-vnav-stop surface={EscapeToken(surface.Source)} {BuildSessionLogFields()}");
    }

    private void TryAnswer(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        string directionSource)
    {
        if (DateTime.UtcNow < nextActionUtc)
        {
            Status = BuildStatus(state, "waiting: action cooldown");
            return;
        }

        if (!IsPlayAction(decision.Action))
        {
            Status = BuildStatus(state, $"blocked: retained action {decision.Action} cannot answer SelectYesno");
            return;
        }

        var prompt = state.SelectYesnoPrompt;
        bool yes;
        string promptLabel;
        if (prompt.Contains("Guess higher?", StringComparison.OrdinalIgnoreCase))
        {
            promptLabel = "Guess higher?";
            yes = decision.Action == HigherLowerAction.PlayHigh;
        }
        else if (prompt.Contains("Guess lower?", StringComparison.OrdinalIgnoreCase))
        {
            promptLabel = "Guess lower?";
            yes = decision.Action == HigherLowerAction.PlayLow;
        }
        else
        {
            Status = BuildStatus(state, $"blocked: unrelated SelectYesno prompt '{prompt}'");
            return;
        }

        nextActionUtc = DateTime.UtcNow + ActionCooldown;
        var answerText = yes ? "yes" : "no";
        var decisionFields = BuildDecisionLogFields(decision, DateTime.UtcNow, directionSource);
        TryStopVnav("selectyesno", force: true);
        if (GameInteractionHelper.TrySelectYesNo(yes, gameGui, log))
        {
            MarkHigherLowerActivity(DateTime.UtcNow, "hlauto-answer", armSession: true);
            Status = BuildStatus(state, $"answer {promptLabel} {answerText}: sent {(yes ? "Yes" : "No")}");
            LogAction($"hlauto answer {EscapeToken(promptLabel)} {answerText} {BuildAddonLogFields(state)} {decisionFields} reason='{Escape(decision.Reason)}'");
            LogDirectionConfirmed(state, decision, "selectyesno", "prompt-answered");
            CommitPlay(decision, "selectyesno");
            return;
        }

        Status = BuildStatus(state, $"answer {promptLabel} {answerText}: SelectYesno click failed");
        LogWarning($"hlauto answer-failed {EscapeToken(promptLabel)} {answerText} {BuildAddonLogFields(state)} {decisionFields} reason='{Escape(decision.Reason)}'");
    }

    private bool TryClickTreasureHighLowButton(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        uint nodeId,
        string label,
        AutomationDecision decision,
        string reason)
    {
        if (DateTime.UtcNow < nextActionUtc)
        {
            Status = BuildStatus(state, $"waiting: {label} cooldown");
            return false;
        }

        nextActionUtc = DateTime.UtcNow + ActionCooldown;
        var decisionFields = BuildDecisionLogFields(decision, DateTime.UtcNow, decision.Retained ? "retained" : "current");
        TryStopVnav($"addon-{label}", force: true);
        if (GameInteractionHelper.TryClickAddonNodeButton(AddonName, nodeId, gameGui, log))
        {
            MarkHigherLowerActivity(DateTime.UtcNow, $"hlauto-click:{label}", armSession: true);
            Status = BuildStatus(state, $"click {label}: {reason}");
            LogAction($"hlauto click {EscapeToken(label)} addon={AddonName} nodeId={nodeId} reason='{Escape(reason)}' {BuildAddonLogFields(state)} {decisionFields}");
            return true;
        }

        Status = BuildStatus(state, $"blocked: {label} node {nodeId} click failed");
        LogWarning($"hlauto click-failed {EscapeToken(label)} addon={AddonName} nodeId={nodeId} reason='{Escape(reason)}' {BuildAddonLogFields(state)} {decisionFields}");
        return false;
    }

    private void TrySelectDirection(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        string directionSource)
    {
        if (!IsPlayAction(decision.Action))
        {
            Status = BuildStatus(state, $"blocked: action {decision.Action} has no direction target");
            return;
        }

        var direction = decision.Action == HigherLowerAction.PlayHigh ? "High" : "Low";
        var target = FindDirectionTarget(direction);
        if (target == null)
        {
            lastDirectionTargetName = direction;
            lastDirectionTargetDistance = null;
            Status = BuildStatus(state, $"blocked: {direction} target not targetable");
            return;
        }

        var distance = DistanceToLocalPlayer(target);
        lastDirectionTargetName = direction;
        lastDirectionTargetDistance = float.IsFinite(distance) ? distance : null;
        if (!float.IsFinite(distance))
        {
            Status = BuildStatus(state, $"blocked: local player unavailable for {direction} target");
            return;
        }

        if (DateTime.UtcNow < nextActionUtc)
        {
            Status = BuildStatus(state, $"waiting: direction cooldown for {direction} at {FormatDistance(distance)}");
            return;
        }

        nextActionUtc = DateTime.UtcNow + ActionCooldown;
        var decisionFields = BuildDecisionLogFields(decision, DateTime.UtcNow, directionSource);
        var targetFields = BuildTargetLogFields(target, distance);
        if (distance > DirectionInteractRange)
        {
            var command = string.Format(
                CultureInfo.InvariantCulture,
                "/vnav moveto {0:F2} {1:F2} {2:F2}",
                target.Position.X,
                target.Position.Y,
                target.Position.Z);
            if (GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
            {
                MarkHigherLowerActivity(DateTime.UtcNow, $"hlauto-nav:{direction}", armSession: true);
                Status = BuildStatus(state, $"navigating-to-direction {direction} at {FormatDistance(distance)}");
                LogAction($"hlauto direction-nav target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
                return;
            }

            Status = BuildStatus(state, $"blocked: vnav moveto failed for {direction}");
            LogWarning($"hlauto direction-nav-failed target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
            return;
        }

        TryStopVnav($"direction-{direction}", force: true);
        if (GameInteractionHelper.TryInteractWithObject(targetManager, target, log))
        {
            MarkHigherLowerActivity(DateTime.UtcNow, $"hlauto-interact:{direction}", armSession: true);
            SetPendingDirectionInteract(decision, direction, target);
            Status = BuildStatus(state, $"direction-interact-sent {direction} for card {FormatCard(decision.Card)}");
            LogAction($"hlauto direction-interact-sent target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
            return;
        }

        Status = BuildStatus(state, $"blocked: interact failed for {direction}");
        LogWarning($"hlauto direction-interact-failed target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
    }

    private IGameObject? FindDirectionTarget(string name)
        => objectTable
            .Where(x => x != null
                        && x.IsTargetable
                        && string.Equals(x.Name.TextValue.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => objectTable.LocalPlayer == null ? float.MaxValue : Vector3.Distance(objectTable.LocalPlayer.Position, x.Position))
            .FirstOrDefault();

    private AutomationDecision? GetRetainedCurrentStepDecision()
        => retainedDecision is { } decision && decision.Step == currentGambleStep
            ? decision
            : null;

    private AutomationDecision? GetPendingCurrentStepDecision()
        => pendingDirectionDecision is { } decision && decision.Step == currentGambleStep
            ? decision
            : null;

    private AutomationDecision? ResolveCurrentDirectionDecision(AutomationDecision? currentDecision, out string directionSource)
    {
        var pending = GetPendingCurrentStepDecision();
        if (pending is not null && IsPlayAction(pending.Action))
        {
            directionSource = "pending-target";
            return pending;
        }

        var retained = GetRetainedCurrentStepDecision();
        if (retained is not null && IsPlayAction(retained.Action))
        {
            directionSource = "retained";
            return retained;
        }

        if (currentDecision is not null
            && currentDecision.Step == currentGambleStep
            && IsPlayAction(currentDecision.Action))
        {
            directionSource = "current";
            return currentDecision;
        }

        directionSource = "waiting-for-decode";
        return null;
    }

    private void CommitPlay(AutomationDecision decision, string source)
    {
        if (IsPlayAction(decision.Action))
        {
            MarkHigherLowerActivity(DateTime.UtcNow, $"hlauto-commit:{source}", armSession: true);
            completedPlayCount = Math.Max(0, completedPlayCount) + 1;
            currentGambleStep = completedPlayCount + 1;
            LogAction($"hlauto step-advance source={EscapeToken(source)} fromStep={decision.Step} nextStep={currentGambleStep} playsCompleted={completedPlayCount} card={FormatCard(decision.Card)} action={decision.Action}");
        }

        retainedDecision = null;
        ClearDirectionState(clearTarget: true);
        lastBlockedReason = string.Empty;
        lastDirectionDecisionSource = "none";
    }

    private void Block(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        string statusSuffix,
        string blockedReason,
        bool incompleteAddon = false)
    {
        TryStopVnav("blocked");
        lastBlockedReason = blockedReason;
        Status = BuildBlockedStatus(state, solverState, statusSuffix, blockedReason);
        var kind = incompleteAddon ? "blocked-incomplete-addon" : "blocked";
        LogBlockOnce(
            $"{activeDutyKey}:{currentGambleStep}:{statusSuffix}:{blockedReason}:{surface.Source}",
            $"hlauto {kind} surface={EscapeToken(surface.Source)} {BuildSessionLogFields()} reason='{Escape(blockedReason)}' {BuildAddonLogFields(state)} solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()} solverReason='{Escape(solverState.Reason)}'");
    }

    private void RecordDatamineSurface(
        DutyContextSnapshot context,
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        AutomationDecision? currentDecision,
        string blockedReason)
    {
        if (!surface.Present && !state.Active)
            return;

        diagnostics.RecordDatamineSurface(
            context.TerritoryTypeId,
            state,
            solverState,
            surface.Source,
            currentGambleStep,
            completedPlayCount,
            retainedDecision is not null,
            retainedDecision?.Step,
            retainedDecision?.Card,
            retainedDecision?.Action.ToString() ?? "None",
            retainedDecision?.Source ?? "none",
            currentDecision?.Card,
            currentDecision?.Action.ToString() ?? "None",
            currentDecision?.Source ?? "none",
            string.IsNullOrWhiteSpace(blockedReason) ? lastBlockedReason : blockedReason);
    }

    private void LogDecisionIfNeeded(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        HigherLowerSurface surface)
    {
        var key = $"{activeDutyKey}:{decision.Step}:{decision.Card?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{decision.Action}:{decision.Source}:{surface.Source}";
        if (key == lastDecisionLogKey)
            return;

        lastDecisionLogKey = key;
        LogAction($"hlauto decision surface={EscapeToken(surface.Source)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, DateTime.UtcNow, decision.Retained ? "retained" : "current")} reason='{Escape(decision.Reason)}'");
    }

    private void LogBlockOnce(string key, string line)
    {
        if (key == lastBlockLogKey)
            return;

        lastBlockLogKey = key;
        LogAction(line);
    }

    private void LogMovementHoldOnce(string line)
    {
        if (line == lastMovementHoldLogKey)
            return;

        lastMovementHoldLogKey = line;
        LogAction(line);
    }

    private void LogAction(string line)
    {
        diagnostics.RecordAutomationLine(line);
        log.Information($"[ADS][HLAUTO] {line}");
    }

    private void LogWarning(string line)
    {
        diagnostics.RecordAutomationLine(line);
        log.Warning($"[ADS][HLAUTO] {line}");
    }

    private void SetPendingDirectionInteract(AutomationDecision decision, string direction, IGameObject target)
    {
        pendingDirectionDecision = decision;
        pendingDirectionName = direction;
        pendingDirectionGameObjectId = target.GameObjectId;
        pendingDirectionInteractUtc = DateTime.UtcNow;
        lastDirectionDecisionSource = "pending-target";
    }

    private bool TryConfirmPendingDirectionTransition(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        string reason)
    {
        var pending = GetPendingCurrentStepDecision();
        if (pending is null || !IsPlayAction(pending.Action))
            return false;

        if (state.HighTargetable || state.LowTargetable)
            return false;

        if (state.SelectYesnoVisible && IsHigherLowerPrompt(state.SelectYesnoPrompt))
            return false;

        var now = DateTime.UtcNow;
        var pendingAge = pendingDirectionInteractUtc == DateTime.MinValue
            ? TimeSpan.Zero
            : now - pendingDirectionInteractUtc;
        var addonReturnedWithReadableCard = state.TreasureHighLowVisible
                                            && !string.Equals(
                                                state.AddonCurrentCardText.Trim(),
                                                "unavailable",
                                                StringComparison.OrdinalIgnoreCase);
        var surfaceSettledClear = !state.Active && pendingAge >= PendingDirectionTransitionSettleDelay;
        if (!addonReturnedWithReadableCard && !surfaceSettledClear)
            return false;

        LogDirectionConfirmed(state, pending, "target-transition", reason);
        Status = BuildStatus(state, $"direction-confirmed {pendingDirectionName}: target transition observed");
        CommitPlay(pending, "target-transition");
        return true;
    }

    private void LogDirectionConfirmed(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        string source,
        string reason)
    {
        var now = DateTime.UtcNow;
        var ageMs = pendingDirectionInteractUtc == DateTime.MinValue
            ? "none"
            : Math.Max(0, (int)(now - pendingDirectionInteractUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        LogAction(
            $"hlauto direction-confirmed source={EscapeToken(source)} reason={EscapeToken(reason)} pendingTarget={EscapeToken(pendingDirectionName)} pendingTargetId=0x{pendingDirectionGameObjectId:X} pendingAgeMs={ageMs} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, source)}");
    }

    private void ClearDirectionState(bool clearTarget)
    {
        pendingDirectionDecision = null;
        pendingDirectionName = string.Empty;
        pendingDirectionGameObjectId = 0;
        pendingDirectionInteractUtc = DateTime.MinValue;
        nextStopCommandUtc = DateTime.MinValue;
        lastStopLogKey = string.Empty;
        if (!clearTarget)
            return;

        lastDirectionTargetName = "none";
        lastDirectionTargetDistance = null;
    }

    private void TryStopVnav(string source, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < nextStopCommandUtc)
            return;

        nextStopCommandUtc = now + ActionCooldown;
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, "/vnav stop", log))
        {
            LogWarning($"hlauto direction-nav-stop-failed source={EscapeToken(source)} {BuildSessionLogFields()}");
            return;
        }

        var logKey = $"{activeDutyKey}:{currentGambleStep}:{source}";
        if (logKey == lastStopLogKey)
            return;

        lastStopLogKey = logKey;
        LogAction($"hlauto direction-nav-stop source={EscapeToken(source)} {BuildSessionLogFields()}");
    }

    private static bool IsAutomationModeAllowed(OwnershipMode ownershipMode, bool hasHigherLowerSurface)
        => IsOwnedAutomationMode(ownershipMode)
           || (ownershipMode == OwnershipMode.Leaving && hasHigherLowerSurface)
           || (ownershipMode == OwnershipMode.Observing && hasHigherLowerSurface);

    private static bool IsOwnedAutomationMode(OwnershipMode ownershipMode)
        => ownershipMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside;

    private static bool ShouldHoldMovementForAutomation(OwnershipMode ownershipMode)
        => IsOwnedAutomationMode(ownershipMode) || ownershipMode == OwnershipMode.Leaving;

    private static bool IsHigherLowerPrompt(string value)
        => ContainsAny(value, "Guess higher?", "Guess lower?", "higher or lower", "higher/lower");

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanStartOpeningPlay(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state)
        => currentGambleStep == 1
           && state.TreasureHighLowVisible
           && IsBlankAddonCard(state);

    private static bool IsBlankAddonCard(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state)
        => string.Equals(state.AddonCurrentCardText.Trim(), "blank", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayAction(HigherLowerAction action)
        => action is HigherLowerAction.PlayHigh or HigherLowerAction.PlayLow;

    private static string ChoiceForAction(HigherLowerAction action)
        => action switch
        {
            HigherLowerAction.PlayHigh => HigherChoice,
            HigherLowerAction.PlayLow => LowerChoice,
            HigherLowerAction.CashOut => OpenChestChoice,
            HigherLowerAction.StartPlay => StartPlayChoice,
            _ => "Blocked",
        };

    private string BuildStatus(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state, string suffix)
        => $"Higher/Lower automation {suffix}; surface={lastSurfaceSource} dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} card={FormatCard(lastDecision?.Card)} action={lastDecision?.Action.ToString() ?? "None"} source='{lastDecision?.Source ?? "none"}' directionSource={lastDirectionDecisionSource} directionTarget={lastDirectionTargetName}@{FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : pendingDirectionName)} pendingAge={FormatPendingAgeSeconds()} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} blockedReason='{lastBlockedReason}' active={state.Active} addonCurrentCard={state.AddonCurrentCardText} addonOtherCard={state.AddonOtherCardText} knownCards={state.KnownCardCount}.";

    private string BuildBlockedStatus(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        string suffix,
        string blockedReason)
        => $"Higher/Lower solver {suffix}; surface={lastSurfaceSource} dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} directionSource={lastDirectionDecisionSource} directionTarget={lastDirectionTargetName}@{FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : pendingDirectionName)} pendingAge={FormatPendingAgeSeconds()} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} blockedReason='{blockedReason}' active={state.Active} addonCurrentCard={state.AddonCurrentCardText} addonOtherCard={state.AddonOtherCardText} decodedCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} solverChoice={solverState.RecommendedChoice} confidence={solverState.Confidence.ToString().ToLowerInvariant()} reason='{solverState.Reason}' source='{solverState.CardSource}' slot={solverState.Slot} textureIndex={(solverState.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}.";

    private string BuildSessionLogFields()
        => $"dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} directionSource={EscapeToken(lastDirectionDecisionSource)} directionTarget={EscapeToken(lastDirectionTargetName)} directionDistance={FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : EscapeToken(pendingDirectionName))} pendingAge={FormatPendingAgeSeconds()} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} retained={(retainedDecision is not null).ToString().ToLowerInvariant()} retainedStep={(retainedDecision?.Step.ToString(CultureInfo.InvariantCulture) ?? "none")} retainedCard={FormatCard(retainedDecision?.Card)} retainedAction={(retainedDecision?.Action.ToString() ?? "None")}";

    private static string BuildAddonLogFields(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state)
        => $"addonCurrentCard={EscapeToken(state.AddonCurrentCardText)} addonOtherCard={EscapeToken(state.AddonOtherCardText)}";

    private static string BuildDecisionLogFields(AutomationDecision decision, DateTime now, string directionSource)
    {
        var retainedAgeMs = decision.Retained
            ? Math.Max(0, (int)(now - decision.ObservedUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
            : "none";
        var retainedSource = decision.Retained ? EscapeToken(decision.Source) : "none";
        return $"step={decision.Step} decodedCard={FormatCard(decision.Card)} action={decision.Action} choice={decision.Choice} source={EscapeToken(decision.Source)} directionSource={EscapeToken(directionSource)} retained={decision.Retained.ToString().ToLowerInvariant()} retainedAgeMs={retainedAgeMs} retainedSource={retainedSource}";
    }

    private static string BuildTargetLogFields(IGameObject target, float distance)
        => string.Format(
            CultureInfo.InvariantCulture,
            "targetId=0x{0:X} distance={1:0.00} range={2:0.00} pos={3:0.00},{4:0.00},{5:0.00}",
            target.GameObjectId,
            distance,
            DirectionInteractRange,
            target.Position.X,
            target.Position.Y,
            target.Position.Z);

    private float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = objectTable.LocalPlayer;
        return player == null ? float.NaN : Vector3.Distance(player.Position, obj.Position);
    }

    private double? GetPendingDirectionInteractAgeSeconds(DateTime now)
        => pendingDirectionDecision is null || pendingDirectionInteractUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (now - pendingDirectionInteractUtc).TotalSeconds);

    private bool IsDutyExitGraceActive(DateTime now)
        => lastHigherLowerActivityUtc != DateTime.MinValue
           && now - lastHigherLowerActivityUtc <= DutyExitQuietGrace;

    private double? GetDutyExitGraceRemainingSeconds(DateTime now)
    {
        if (lastHigherLowerActivityUtc == DateTime.MinValue)
            return null;

        var remaining = DutyExitQuietGrace - (now - lastHigherLowerActivityUtc);
        return Math.Max(0, remaining.TotalSeconds);
    }

    private string FormatPendingAgeSeconds()
    {
        var age = GetPendingDirectionInteractAgeSeconds(DateTime.UtcNow);
        return age.HasValue
            ? age.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string FormatDutyExitGraceRemainingSeconds()
    {
        var remaining = GetDutyExitGraceRemainingSeconds(DateTime.UtcNow);
        return remaining.HasValue
            ? remaining.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private static string FormatDistance(float? distance)
        => distance.HasValue
            ? distance.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "unknown";

    private static string FormatCard(int? card)
        => card?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string EscapeToken(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : Escape(value).Replace(' ', '_');

    private static string Escape(string value)
        => HigherLowerCardVfxSolverService.Escape(value);

    private readonly record struct HigherLowerSurface(bool Present, string Source);

    private enum HigherLowerAction
    {
        PlayHigh = 0,
        PlayLow = 1,
        CashOut = 2,
        StartPlay = 3,
    }

    private sealed record AutomationDecision(
        int Step,
        int? Card,
        HigherLowerAction Action,
        string Choice,
        string Source,
        bool Retained,
        DateTime ObservedUtc,
        string Reason);

    public sealed record HigherLowerAutomationDebugState(
        bool Enabled,
        bool HoldMovement,
        string Status,
        uint SessionDutyKey,
        int SessionStep,
        int? Card,
        string Action,
        string Source,
        string DirectionSource,
        string Surface,
        string BlockedReason,
        bool Retained,
        int? RetainedStep,
        int? RetainedCard,
        string RetainedAction,
        string RetainedSource,
        string DirectionTargetName,
        float? DirectionTargetDistance,
        string PendingDirectionTarget,
        double? PendingDirectionInteractAgeSeconds,
        bool BlocksDutyExit,
        DateTime LastHigherLowerActivityUtc,
        string LastHigherLowerActivitySource,
        double? DutyExitGraceRemainingSeconds);
}
