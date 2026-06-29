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
    private const int TryLuckCallbackArg = 0;
    private const int OpenChestCallbackArg = 1;
    private const int SubmitCallbackArg = -2;
    private const string HigherChoice = HigherLowerCardVfxSolverService.HigherChoice;
    private const string LowerChoice = HigherLowerCardVfxSolverService.LowerChoice;
    private const string OpenChestChoice = HigherLowerCardVfxSolverService.OpenChestChoice;
    private const string StartPlayChoice = "StartPlay";
    private const float DirectionInteractRange = 2.0f;
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan PromptAnswerRetryCooldown = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PendingCallbackTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PendingDirectionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DutyExitQuietGrace = TimeSpan.FromSeconds(15);
    private const string LockJamMessage = "The lock jams, and the coffer vanishes before your very eyes";
    private const string VaultKeyMessage = "You obtain a vault key.";

    private readonly TreasureHighLowDiagnosticService diagnostics;
    private readonly HigherLowerCardVfxSolverService solver;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime nextPromptAnswerUtc = DateTime.MinValue;
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
    private AutomationDecision? pendingCallbackDecision;
    private AutomationDecision? lastDecision;
    private string lastBlockedReason = string.Empty;
    private string lastSurfaceSource = "none";
    private string lastDirectionDecisionSource = "none";
    private string lastDirectionTargetName = "none";
    private float? lastDirectionTargetDistance;
    private string pendingDirectionName = string.Empty;
    private ulong pendingDirectionGameObjectId;
    private DateTime pendingDirectionStartedUtc = DateTime.MinValue;
    private DateTime pendingDirectionPhaseStartedUtc = DateTime.MinValue;
    private PendingDirectionPhase pendingDirectionPhase = PendingDirectionPhase.None;
    private PendingDirectionPhase pendingDirectionTimedOutPhase = PendingDirectionPhase.None;
    private ulong pendingDirectionBaselineServerRowSequence;
    private bool pendingDirectionHasBaselineServerRowSequence;
    private bool pendingDirectionTimeoutLogged;
    private int pendingDirectionInteractAttempts;
    private DateTime pendingDirectionLastInteractUtc = DateTime.MinValue;
    private DateTime pendingDirectionNextRetryUtc = DateTime.MinValue;
    private string pendingDirectionLastRetryReason = "none";
    private string lastDirectionTerminalProof = "none";
    private PendingCallbackAction pendingCallbackAction = PendingCallbackAction.None;
    private PendingCallbackPhase pendingCallbackPhase = PendingCallbackPhase.None;
    private PendingCallbackPhase pendingCallbackTimedOutPhase = PendingCallbackPhase.None;
    private DateTime pendingCallbackSentUtc = DateTime.MinValue;
    private DateTime pendingCallbackPhaseStartedUtc = DateTime.MinValue;
    private ulong pendingCallbackBaselineServerRowSequence;
    private bool pendingCallbackHasBaselineServerRowSequence;
    private bool pendingCallbackConfirmVisibleLogged;
    private bool pendingCallbackTimeoutLogged;
    private DateTime lastHigherLowerActivityUtc = DateTime.MinValue;
    private string lastHigherLowerActivitySource = "none";
    private OwnershipMode lastOwnershipMode = OwnershipMode.Idle;

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
        var now = DateTime.UtcNow;
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
            PendingDirectionPhase: FormatPendingDirectionPhase(pendingDirectionPhase),
            PendingDirectionAgeSeconds: GetPendingDirectionAgeSeconds(now),
            PendingDirectionPhaseAgeSeconds: GetPendingDirectionPhaseAgeSeconds(now),
            PendingDirectionInteractAttempts: pendingDirectionInteractAttempts,
            PendingDirectionMaxInteractAttempts: HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts,
            PendingDirectionLastInteractUtc: pendingDirectionLastInteractUtc == DateTime.MinValue ? null : pendingDirectionLastInteractUtc,
            PendingDirectionLastInteractAgeSeconds: GetPendingDirectionLastInteractAgeSeconds(now),
            PendingDirectionNextRetryUtc: pendingDirectionNextRetryUtc == DateTime.MinValue ? null : pendingDirectionNextRetryUtc,
            PendingDirectionNextRetryInSeconds: GetPendingDirectionNextRetryInSeconds(now),
            PendingDirectionLastRetryReason: pendingDirectionLastRetryReason,
            PendingDirectionBaselineServerRowSequence: pendingDirectionHasBaselineServerRowSequence ? pendingDirectionBaselineServerRowSequence : null,
            PendingDirectionTerminalProof: lastDirectionTerminalProof,
            PendingCallbackAction: FormatPendingCallbackAction(pendingCallbackAction),
            PendingCallbackPhase: FormatPendingCallbackPhase(pendingCallbackPhase),
            PendingCallbackAgeSeconds: GetPendingCallbackAgeSeconds(now),
            BlocksDutyExit: BlocksDutyExit,
            LastHigherLowerActivityUtc: lastHigherLowerActivityUtc,
            LastHigherLowerActivitySource: lastHigherLowerActivitySource,
            DutyExitGraceRemainingSeconds: GetDutyExitGraceRemainingSeconds(now));
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
            ClearPendingCallbackState();
            lastMovementHoldLogKey = string.Empty;
            Status = "Higher/Lower automation disabled.";
            log.Information("[ADS][HLAUTO] disabled.");
            return;
        }

        Status = BuildStatus(diagnostics.CaptureRuntimeState(), "enabled");
        log.Information("[ADS][HLAUTO] enabled.");
    }

    public bool HandleChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var now = DateTime.UtcNow;
        if (message.Contains(LockJamMessage, StringComparison.OrdinalIgnoreCase))
        {
            CompleteHigherLowerTerminalFromChat("lock-jam", now);
            return true;
        }

        if (message.Contains(VaultKeyMessage, StringComparison.OrdinalIgnoreCase)
            && HasHigherLowerTerminalContext(now))
        {
            CompleteHigherLowerTerminalFromChat("vault-key", now);
            return true;
        }

        return false;
    }

    public void Update(DutyContextSnapshot context, OwnershipMode ownershipMode, bool pluginEnabled)
    {
        HoldMovement = false;
        BlocksDutyExit = false;
        var now = DateTime.UtcNow;
        var state = diagnostics.CaptureRuntimeState();
        var solverState = solver.CurrentState;
        ResetSessionForContext(context);

        var enteredLeaving = ownershipMode == OwnershipMode.Leaving && lastOwnershipMode != OwnershipMode.Leaving;
        lastOwnershipMode = ownershipMode;
        if (enteredLeaving)
            ClearHigherLowerActivitySession();

        var surface = DetectSurface(state);
        lastSurfaceSource = surface.Source;
        UpdateHigherLowerActivity(now, context, ownershipMode, surface);
        BlocksDutyExit = ownershipMode == OwnershipMode.Leaving && context.InInstancedDuty && IsDutyExitGraceActive(now);
        var currentDecision = TryBuildCurrentDecision(state, solverState, now, out var cardBlockedReason);
        if (currentDecision is not null)
        {
            lastDecision = currentDecision;
            lastBlockedReason = string.Empty;
        }
        RecordDatamineSurface(context, state, solverState, surface, currentDecision, cardBlockedReason);

        var modeAllowed = IsAutomationModeAllowed(ownershipMode, surface.Present)
                          || HasPendingCallback()
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
            if (HasPendingCallback())
            {
                if (ShouldHoldMovementForAutomation(ownershipMode))
                    HoldMovement = true;

                TryHandlePendingCallback(state, solverState, surface, now);
                return;
            }

            if (pendingDirectionDecision is not null)
            {
                if (ShouldHoldMovementForAutomation(ownershipMode))
                    HoldMovement = true;
                TryHandlePendingDirection(state, solverState, surface, now);
                return;
            }

            lastMovementHoldLogKey = string.Empty;
            lastBlockedReason = string.Empty;
            lastDirectionDecisionSource = "none";
            ClearDirectionState(clearTarget: true, clearTerminalProof: false);
            Status = configuration.HigherLowerAutomationEnabled
                ? BuildStatus(state, "waiting")
                : "Higher/Lower suggested mode waiting.";
            return;
        }

        ApplyMovementHold(ownershipMode, surface);

        if (currentDecision is not null)
            LogDecisionIfNeeded(state, currentDecision, surface);

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

        if (HasPendingCallback())
        {
            ApplyMovementHold(ownershipMode, surface);
            if (TryHandlePendingCallback(state, solverState, surface, now))
                return;
        }

        if (pendingDirectionDecision is not null)
        {
            ApplyMovementHold(ownershipMode, surface);
            if (TryHandlePendingDirection(state, solverState, surface, now))
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
            TryAnswer(state, solverState, promptDecision, promptDecisionSource);
            return;
        }

        if (state.TreasureHighLowVisible)
        {
            if (currentDecision is not null)
            {
                if (currentDecision.Action == HigherLowerAction.CashOut)
                {
                    TrySendTreasureHighLowCallbackSequence(
                        state,
                        solverState,
                        PendingCallbackAction.OpenChest,
                        OpenChestCallbackArg,
                        currentDecision,
                        currentDecision.Reason);
                    return;
                }

                if (IsPlayAction(currentDecision.Action))
                {
                    lastDirectionDecisionSource = "current";
                    TrySendTreasureHighLowCallbackSequence(
                        state,
                        solverState,
                        PendingCallbackAction.TryLuck,
                        TryLuckCallbackArg,
                        currentDecision,
                        currentDecision.Reason);
                    return;
                }
            }

            if (CanStartOpeningPlay(state, cardBlockedReason))
            {
                var openingDecision = BuildOpeningStartDecision(state, now);
                lastDecision = openingDecision;
                LogDecisionIfNeeded(state, openingDecision, surface);
                TrySendTreasureHighLowCallbackSequence(
                    state,
                    solverState,
                    PendingCallbackAction.TryLuck,
                    TryLuckCallbackArg,
                    openingDecision,
                    openingDecision.Reason);
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
            if (currentDecision is { Action: HigherLowerAction.CashOut } cashOutDecision)
            {
                BeginOrContinuePendingOpenChestSurfaceWait(state, solverState, surface, cashOutDecision, now);
                return;
            }

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

            TrySelectDirection(state, solverState, directionDecision, directionSource);
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
        ClearPendingCallbackState();
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
        OwnershipMode ownershipMode,
        HigherLowerSurface surface)
    {
        if (!context.InInstancedDuty || ownershipMode != OwnershipMode.Leaving)
        {
            ClearHigherLowerActivitySession();
            return;
        }

        if (surface.Present)
        {
            MarkHigherLowerActivity(now, surface.Source);
            return;
        }

        if (!IsDutyExitGraceActive(now))
            ClearHigherLowerActivitySession();
    }

    private void MarkHigherLowerActivity(DateTime timestampUtc, string source)
    {
        if (timestampUtc < lastHigherLowerActivityUtc)
            return;

        lastHigherLowerActivityUtc = timestampUtc;
        lastHigherLowerActivitySource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    private void ClearHigherLowerActivitySession()
    {
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

        var hasSolverCard = TryGetTrustedSolverCard(solverState, out var solverCard, out var solverSource);
        var hasVisualCard = state.CardSourceSafe && state.CurrentCard is >= 1 and <= 9;
        var solverSkipsVisualMismatch = hasSolverCard
                                       && IsPostSelectionRevealSolverState(solverState);
        if (!solverSkipsVisualMismatch
            && hasSolverCard
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
            card = trustedCard.Value;
            source = trustedSource;
            return true;
        }

        if (state.TreasureHighLowVisible && IsBlankAddonCard(state))
        {
            blockedReason = "addon current card is blank";
            return false;
        }

        if (state.AddonCurrentCard is >= 1 and <= 9)
        {
            blockedReason = string.Create(
                CultureInfo.InvariantCulture,
                $"no trusted runtime card decode; addon atk ignored; addonCard={state.AddonCurrentCard.Value}; currentGraphicKey='{Escape(state.CurrentGraphicKey)}'; solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()}; solverReason='{Escape(solverState.Reason)}'");
            return false;
        }

        blockedReason = string.Create(
            CultureInfo.InvariantCulture,
            $"no trusted runtime card decode; addonCurrentCard={state.AddonCurrentCardText}; solverCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; solverConfidence={solverState.Confidence.ToString().ToLowerInvariant()}; solverReason='{Escape(solverState.Reason)}'");
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
            || IsUntrustedSolverCardSource(source))
        {
            return false;
        }

        card = solverState.CurrentCard.Value;
        return true;
    }

    private static bool IsUntrustedSolverCardSource(string source)
        => string.Equals(source, HigherLowerCardVfxSolverService.AddonAtkValueSource, StringComparison.OrdinalIgnoreCase)
           || source.StartsWith($"{HigherLowerCardVfxSolverService.AddonAtkValueSource}+", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, HigherLowerCardVfxSolverService.StaticAvfxMetadataSource, StringComparison.OrdinalIgnoreCase)
           || source.StartsWith($"{HigherLowerCardVfxSolverService.StaticAvfxMetadataSource}+", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostSelectionRevealSolverState(HigherLowerCardVfxSolverService.SolverState solverState)
        => string.Equals(solverState.CardSource, HigherLowerCardVfxSolverService.ServerEObjAnimSource, StringComparison.OrdinalIgnoreCase)
           && string.Equals(solverState.Slot, "right-reveal", StringComparison.OrdinalIgnoreCase)
           && string.Equals(solverState.Reason, "accepted-newer-right-reveal", StringComparison.OrdinalIgnoreCase);

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
            Source: state.AddonCurrentCard is >= 1 and <= 9 ? "opening-untrusted-addon-atk" : "opening-no-trusted-card",
            Retained: false,
            ObservedUtc: now,
            Reason: state.AddonCurrentCard is >= 1 and <= 9
                ? $"step 1 starts Higher/Lower despite ignored addon ATK card {state.AddonCurrentCard.Value}"
                : "step 1 starts Higher/Lower; no trusted runtime card yet");

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
        HigherLowerCardVfxSolverService.SolverState solverState,
        AutomationDecision decision,
        string directionSource)
    {
        var now = DateTime.UtcNow;
        if (now < nextPromptAnswerUtc)
        {
            Status = BuildStatus(state, "waiting: prompt answer retry cooldown");
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

        nextPromptAnswerUtc = now + PromptAnswerRetryCooldown;
        var answerText = yes ? "yes" : "no";
        var decisionFields = BuildDecisionLogFields(decision, now, directionSource);
        CaptureServerRowBaseline(solverState, out var baselineServerRowSequence, out var hasBaselineServerRowSequence);
        TryStopVnav("selectyesno", force: true);
        if (GameInteractionHelper.TrySelectYesNo(yes, gameGui, log))
        {
            MarkHigherLowerActivity(now, "hlauto-answer");
            BeginPendingDirection(decision, DirectionNameForAction(decision.Action), 0, PendingDirectionPhase.WaitingProof, now, directionSource, baselineServerRowSequence, hasBaselineServerRowSequence);
            Status = BuildStatus(state, $"answer {promptLabel} {answerText}: sent {(yes ? "Yes" : "No")}");
            LogAction($"hlauto answer {EscapeToken(promptLabel)} {answerText} {BuildAddonLogFields(state)} {decisionFields} reason='{Escape(decision.Reason)}'");
            LogAction($"hlauto direction-confirm-answered action={decision.Action} answer={(yes ? "Yes" : "No")} prompt='{Escape(state.SelectYesnoPrompt)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, directionSource)} {BuildPendingDirectionLogFields()}");
            return;
        }

        Status = BuildStatus(state, $"answer {promptLabel} {answerText}: SelectYesno click failed");
        LogWarning($"hlauto answer-failed {EscapeToken(promptLabel)} {answerText} {BuildAddonLogFields(state)} {decisionFields} reason='{Escape(decision.Reason)}'");
    }

    private bool TrySendTreasureHighLowCallbackSequence(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        PendingCallbackAction action,
        int actionArg,
        AutomationDecision decision,
        string reason)
    {
        var actionName = FormatPendingCallbackAction(action);
        if (!state.TreasureHighLowVisible)
        {
            Status = BuildStatus(state, $"waiting: {actionName} TreasureHighLow surface");
            return false;
        }

        if (DateTime.UtcNow < nextActionUtc)
        {
            Status = BuildStatus(state, $"waiting: {actionName} callback cooldown");
            return false;
        }

        var now = DateTime.UtcNow;
        nextActionUtc = now + ActionCooldown;
        var decisionFields = BuildDecisionLogFields(decision, now, decision.Retained ? "retained" : "current");
        CaptureServerRowBaseline(solverState, out var baselineServerRowSequence, out var hasBaselineServerRowSequence);
        TryStopVnav($"callback-{actionName}", force: true);
        var actionSent = GameInteractionHelper.TryFireAddonCallback(AddonName, true, actionArg);
        var submitSent = GameInteractionHelper.TryFireAddonCallback(AddonName, true, SubmitCallbackArg);
        var sequence = $"{actionArg.ToString(CultureInfo.InvariantCulture)},{SubmitCallbackArg.ToString(CultureInfo.InvariantCulture)}";
        if (actionSent)
        {
            BeginPendingCallback(action, decision, now, baselineServerRowSequence, hasBaselineServerRowSequence);
            MarkHigherLowerActivity(now, $"hlauto-callback:{actionName}");
            Status = submitSent
                ? BuildStatus(state, $"callback-sent {actionName}; waiting proof")
                : BuildStatus(state, $"callback-partial-sent {actionName}; waiting proof");
            var sendKind = submitSent ? "callback-sent" : "callback-partial-sent";
            LogAction($"hlauto {sendKind} action={actionName} sequence='{sequence}' actionSent={actionSent.ToString().ToLowerInvariant()} submitSent={submitSent.ToString().ToLowerInvariant()} reason='{Escape(reason)}' {BuildAddonLogFields(state)} {decisionFields} {BuildPendingCallbackLogFields()}");
            return true;
        }

        Status = BuildStatus(state, $"blocked: {actionName} callback send failed");
        LogWarning($"hlauto callback-send-failed action={actionName} sequence='{sequence}' actionSent={actionSent.ToString().ToLowerInvariant()} submitSent={submitSent.ToString().ToLowerInvariant()} reason='{Escape(reason)}' {BuildAddonLogFields(state)} {decisionFields}");
        return false;
    }

    private void BeginPendingCallback(
        PendingCallbackAction action,
        AutomationDecision decision,
        DateTime now,
        ulong baselineServerRowSequence,
        bool hasBaselineServerRowSequence)
    {
        pendingCallbackAction = action;
        pendingCallbackPhase = PendingCallbackPhase.WaitingSelectYesno;
        pendingCallbackTimedOutPhase = PendingCallbackPhase.None;
        pendingCallbackDecision = decision;
        pendingCallbackSentUtc = now;
        pendingCallbackPhaseStartedUtc = now;
        pendingCallbackConfirmVisibleLogged = false;
        pendingCallbackTimeoutLogged = false;
        retainedDecision = null;
        pendingCallbackBaselineServerRowSequence = baselineServerRowSequence;
        pendingCallbackHasBaselineServerRowSequence = hasBaselineServerRowSequence;
    }

    private void BeginOrContinuePendingOpenChestSurfaceWait(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        AutomationDecision decision,
        DateTime now)
    {
        var recovery = HigherLowerCashOutRecoveryPolicy.Evaluate(new HigherLowerCashOutRecoveryPolicy.RecoveryState(
            CashOutDecision: decision.Action == HigherLowerAction.CashOut,
            PendingOpenChest: IsPendingOpenChestCallback(),
            PendingPhase: ToCashOutRecoveryPendingPhase(pendingCallbackPhase),
            TreasureHighLowVisible: state.TreasureHighLowVisible,
            HighLowTargetable: state.HighTargetable || state.LowTargetable,
            TerminalProofAvailable: false,
            TimedOut: IsPendingCallbackTimedOut(now)));

        if (recovery.Action == HigherLowerCashOutRecoveryPolicy.RecoveryAction.SendOpenChest)
        {
            TrySendTreasureHighLowCallbackSequence(
                state,
                solverState,
                PendingCallbackAction.OpenChest,
                OpenChestCallbackArg,
                decision,
                decision.Reason);
            return;
        }

        if (recovery.Action == HigherLowerCashOutRecoveryPolicy.RecoveryAction.Timeout)
        {
            TimeoutPendingCallback(state, solverState, surface, now, recovery.Reason);
            return;
        }

        if (recovery.Action == HigherLowerCashOutRecoveryPolicy.RecoveryAction.StartWaitingSurface)
        {
            CaptureServerRowBaseline(solverState, out var baselineServerRowSequence, out var hasBaselineServerRowSequence);
            BeginPendingCallbackWaitingSurface(decision, now, baselineServerRowSequence, hasBaselineServerRowSequence);
            LogAction($"hlauto cashout-openchest-waiting-surface action=OpenChest reason='{Escape(decision.Reason)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "cashout-recovery")} {BuildPendingCallbackLogFields()}");
        }

        TryStopVnav("cashout-openchest-waiting-surface");
        MarkHigherLowerActivity(now, "hlauto-cashout-openchest-waiting-surface");
        Status = BuildStatus(state, "waiting: OpenChest TreasureHighLow surface");
    }

    private void BeginPendingCallbackWaitingSurface(
        AutomationDecision decision,
        DateTime now,
        ulong baselineServerRowSequence,
        bool hasBaselineServerRowSequence)
    {
        ClearDirectionState(clearTarget: true);
        pendingCallbackAction = PendingCallbackAction.OpenChest;
        pendingCallbackPhase = PendingCallbackPhase.WaitingSurface;
        pendingCallbackTimedOutPhase = PendingCallbackPhase.None;
        pendingCallbackDecision = decision;
        pendingCallbackSentUtc = DateTime.MinValue;
        pendingCallbackPhaseStartedUtc = now;
        pendingCallbackConfirmVisibleLogged = false;
        pendingCallbackTimeoutLogged = false;
        retainedDecision = null;
        pendingCallbackBaselineServerRowSequence = baselineServerRowSequence;
        pendingCallbackHasBaselineServerRowSequence = hasBaselineServerRowSequence;
        lastDirectionDecisionSource = "cashout-openchest";
    }

    private bool TryHandlePendingCallback(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now)
    {
        if (!HasPendingCallback())
            return false;

        var decision = pendingCallbackDecision;
        if (decision is null)
        {
            TimeoutPendingCallback(state, solverState, surface, now, "missing-decision");
            return true;
        }

        if (pendingCallbackPhase == PendingCallbackPhase.TimedOut)
        {
            Block(
                state,
                solverState,
                surface,
                "blocked: callback timeout",
                BuildPendingCallbackTimeoutReason(),
                incompleteAddon: true);
            return true;
        }

        if (pendingCallbackPhase == PendingCallbackPhase.WaitingSurface)
            return TryHandlePendingOpenChestSurface(state, solverState, surface, now, decision);

        if (pendingCallbackPhase == PendingCallbackPhase.WaitingSelectYesno)
            return TryHandlePendingCallbackSelectYesno(state, solverState, surface, now, decision);

        if (pendingCallbackPhase == PendingCallbackPhase.WaitingProof)
            return TryHandlePendingCallbackProof(state, solverState, surface, now, decision);

        return false;
    }

    private bool TryHandlePendingOpenChestSurface(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now,
        AutomationDecision decision)
    {
        var recovery = HigherLowerCashOutRecoveryPolicy.Evaluate(new HigherLowerCashOutRecoveryPolicy.RecoveryState(
            CashOutDecision: decision.Action == HigherLowerAction.CashOut,
            PendingOpenChest: IsPendingOpenChestCallback(),
            PendingPhase: ToCashOutRecoveryPendingPhase(pendingCallbackPhase),
            TreasureHighLowVisible: state.TreasureHighLowVisible,
            HighLowTargetable: state.HighTargetable || state.LowTargetable,
            TerminalProofAvailable: false,
            TimedOut: IsPendingCallbackTimedOut(now)));

        if (recovery.Action == HigherLowerCashOutRecoveryPolicy.RecoveryAction.Timeout)
        {
            TimeoutPendingCallback(state, solverState, surface, now, recovery.Reason);
            return true;
        }

        if (recovery.Action == HigherLowerCashOutRecoveryPolicy.RecoveryAction.SendOpenChest)
        {
            TrySendTreasureHighLowCallbackSequence(
                state,
                solverState,
                PendingCallbackAction.OpenChest,
                OpenChestCallbackArg,
                decision,
                decision.Reason);
            return true;
        }

        TryStopVnav("cashout-openchest-waiting-surface");
        MarkHigherLowerActivity(now, "hlauto-cashout-openchest-waiting-surface");
        Status = BuildStatus(state, "waiting: OpenChest TreasureHighLow surface");
        return true;
    }

    private bool TryHandlePendingCallbackSelectYesno(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now,
        AutomationDecision decision)
    {
        if (TryGetPendingCallbackProof(state, solverState, out var proof, out var serverRowSequence))
        {
            ConfirmPendingCallback(state, decision, now, proof, serverRowSequence);
            return true;
        }

        if (!state.SelectYesnoVisible)
        {
            if (IsPendingCallbackTimedOut(now))
                TimeoutPendingCallback(state, solverState, surface, now);
            else
                Status = BuildStatus(state, $"waiting: callback confirm {FormatPendingCallbackAction(pendingCallbackAction)}");

            return true;
        }

        if (!pendingCallbackConfirmVisibleLogged)
        {
            pendingCallbackConfirmVisibleLogged = true;
            LogAction($"hlauto callback-confirm-visible action={FormatPendingCallbackAction(pendingCallbackAction)} prompt='{Escape(state.SelectYesnoPrompt)}' {BuildAddonLogFields(state)} {BuildPendingCallbackLogFields()}");
        }

        if (now < nextActionUtc)
        {
            Status = BuildStatus(state, $"waiting: callback confirm cooldown {FormatPendingCallbackAction(pendingCallbackAction)}");
            return true;
        }

        nextActionUtc = now + ActionCooldown;
        TryStopVnav("callback-confirm", force: true);
        if (GameInteractionHelper.TrySelectYesNo(true, gameGui, log))
        {
            pendingCallbackPhase = PendingCallbackPhase.WaitingProof;
            pendingCallbackPhaseStartedUtc = now;
            MarkHigherLowerActivity(now, "hlauto-callback-confirm");
            Status = BuildStatus(state, $"callback confirm answered {FormatPendingCallbackAction(pendingCallbackAction)}; waiting proof");
            LogAction($"hlauto callback-confirm-answered action={FormatPendingCallbackAction(pendingCallbackAction)} answer=Yes prompt='{Escape(state.SelectYesnoPrompt)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "callback-confirm")} {BuildPendingCallbackLogFields()}");
            return true;
        }

        Status = BuildStatus(state, $"blocked: callback confirm Yes failed {FormatPendingCallbackAction(pendingCallbackAction)}");
        LogWarning($"hlauto callback-confirm-answer-failed action={FormatPendingCallbackAction(pendingCallbackAction)} answer=Yes prompt='{Escape(state.SelectYesnoPrompt)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "callback-confirm")} {BuildPendingCallbackLogFields()}");
        return true;
    }

    private bool TryHandlePendingCallbackProof(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now,
        AutomationDecision decision)
    {
        if (TryGetPendingCallbackProof(state, solverState, out var proof, out var serverRowSequence))
        {
            ConfirmPendingCallback(state, decision, now, proof, serverRowSequence);
            return true;
        }

        if (IsPendingCallbackTimedOut(now))
        {
            TimeoutPendingCallback(state, solverState, surface, now);
            return true;
        }

        Status = BuildStatus(state, $"waiting: callback proof {FormatPendingCallbackAction(pendingCallbackAction)}");
        return true;
    }

    private void ConfirmPendingCallback(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        DateTime now,
        string proof,
        ulong? serverRowSequence)
    {
        var action = pendingCallbackAction;
        var actionName = FormatPendingCallbackAction(action);
        var proofFields = serverRowSequence.HasValue
            ? $"proof={EscapeToken(proof)} sourceRowSeq={serverRowSequence.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"proof={EscapeToken(proof)}";
        LogAction($"hlauto callback-confirmed action={actionName} {proofFields} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "callback-proof")} {BuildPendingCallbackLogFields()}");

        var shouldRetainDirection = action == PendingCallbackAction.TryLuck
                                    && IsPlayAction(decision.Action)
                                    && IsDirectionProof(proof);

        ClearPendingCallbackState();
        lastBlockedReason = string.Empty;
        if (shouldRetainDirection)
        {
            retainedDecision = decision with { Retained = true, ObservedUtc = now };
            lastDirectionDecisionSource = "retained";
        }
        else if (action == PendingCallbackAction.OpenChest)
        {
            retainedDecision = null;
            ClearDirectionState(clearTarget: true);
            lastDirectionDecisionSource = "none";
        }

        Status = BuildStatus(state, $"callback-confirmed {actionName} proof={proof}");
    }

    private void TimeoutPendingCallback(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now,
        string reason = "")
    {
        var timeoutPhase = pendingCallbackPhase;
        pendingCallbackTimedOutPhase = timeoutPhase;
        pendingCallbackPhase = PendingCallbackPhase.TimedOut;
        var actionName = FormatPendingCallbackAction(pendingCallbackAction);
        var reasonField = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" reason={EscapeToken(reason)}";
        if (!pendingCallbackTimeoutLogged)
        {
            pendingCallbackTimeoutLogged = true;
            var eventName = pendingCallbackAction == PendingCallbackAction.OpenChest
                            && timeoutPhase == PendingCallbackPhase.WaitingSurface
                ? "cashout-surface-timeout"
                : "callback-timeout";
            LogWarning($"hlauto {eventName} action={actionName} phase={FormatPendingCallbackPhase(timeoutPhase)}{reasonField} age={FormatPendingCallbackAgeSeconds(now)} {BuildAddonLogFields(state)} {BuildPendingCallbackLogFields()}");
        }

        Block(
            state,
            solverState,
            surface,
            "blocked: callback timeout",
            BuildPendingCallbackTimeoutReason(),
            incompleteAddon: true);
    }

    private void TrySelectDirection(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
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
                Status = BuildStatus(state, $"navigating-to-direction {direction} at {FormatDistance(distance)}");
                LogAction($"hlauto direction-nav target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
                return;
            }

            Status = BuildStatus(state, $"blocked: vnav moveto failed for {direction}");
            LogWarning($"hlauto direction-nav-failed target={EscapeToken(direction)} {targetFields} {BuildAddonLogFields(state)} {decisionFields}");
            return;
        }

        TryStopVnav($"direction-{direction}", force: true);
        CaptureServerRowBaseline(solverState, out var baselineServerRowSequence, out var hasBaselineServerRowSequence);
        if (GameInteractionHelper.TryInteractWithObject(targetManager, target, log))
        {
            var now = DateTime.UtcNow;
            MarkHigherLowerActivity(now, $"hlauto-interact:{direction}");
            BeginPendingDirection(decision, direction, target.GameObjectId, PendingDirectionPhase.WaitingSelectYesno, now, directionSource, baselineServerRowSequence, hasBaselineServerRowSequence);
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
                        && IsDirectionTarget(x, name))
            .OrderBy(x => objectTable.LocalPlayer == null ? float.MaxValue : Vector3.Distance(objectTable.LocalPlayer.Position, x.Position))
            .FirstOrDefault();

    private IGameObject? FindPendingDirectionRetryTarget(string name)
    {
        if (pendingDirectionGameObjectId != 0)
        {
            var original = objectTable.FirstOrDefault(x => x != null
                                                           && x.GameObjectId == pendingDirectionGameObjectId
                                                           && IsDirectionTarget(x, name));
            if (original is not null)
                return original;
        }

        return FindDirectionTarget(name);
    }

    private static bool IsDirectionTarget(IGameObject obj, string name)
        => obj.IsTargetable
           && string.Equals(obj.Name.TextValue.Trim(), name, StringComparison.OrdinalIgnoreCase);

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

    private void BeginPendingDirection(
        AutomationDecision decision,
        string direction,
        ulong targetGameObjectId,
        PendingDirectionPhase phase,
        DateTime now,
        string directionSource,
        ulong baselineServerRowSequence,
        bool hasBaselineServerRowSequence)
    {
        pendingDirectionDecision = decision;
        pendingDirectionName = direction;
        pendingDirectionGameObjectId = targetGameObjectId;
        pendingDirectionStartedUtc = now;
        pendingDirectionPhaseStartedUtc = now;
        pendingDirectionPhase = phase;
        pendingDirectionTimedOutPhase = PendingDirectionPhase.None;
        pendingDirectionTimeoutLogged = false;
        if (phase == PendingDirectionPhase.WaitingSelectYesno)
        {
            pendingDirectionInteractAttempts = 1;
            pendingDirectionLastInteractUtc = now;
            pendingDirectionNextRetryUtc = HigherLowerPendingDirectionRetryPolicy.GetInitialNextRetryUtc(now);
            pendingDirectionLastRetryReason = "initial-interact-sent";
        }
        else
        {
            pendingDirectionInteractAttempts = 0;
            pendingDirectionLastInteractUtc = DateTime.MinValue;
            pendingDirectionNextRetryUtc = DateTime.MinValue;
            pendingDirectionLastRetryReason = "none";
        }
        lastDirectionTerminalProof = "none";
        lastDirectionDecisionSource = phase == PendingDirectionPhase.WaitingSelectYesno
            ? "pending-target"
            : directionSource;

        pendingDirectionBaselineServerRowSequence = baselineServerRowSequence;
        pendingDirectionHasBaselineServerRowSequence = hasBaselineServerRowSequence;

        LogAction($"hlauto direction-pending action={decision.Action} phase={FormatPendingDirectionPhase(phase)} baselineServerRowSeq={FormatPendingDirectionBaselineServerRowSequence()} {BuildDecisionLogFields(decision, now, directionSource)}");
    }

    private bool TryHandlePendingDirection(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now)
    {
        var pending = pendingDirectionDecision;
        if (pending is null)
            return false;

        TryStopVnav("pending-direction");

        if (pendingDirectionPhase == PendingDirectionPhase.TimedOut)
        {
            if (!surface.Present)
                CompletePendingDirectionTerminal(state, pending, "surface-cleared-stale-objects", now);
            else
                BlockPendingDirectionTimeout(state, solverState, surface, pending);

            return true;
        }

        if (state.SelectYesnoVisible
            && IsHigherLowerPrompt(state.SelectYesnoPrompt))
        {
            TryAnswerPendingDirectionConfirm(state, pending, now);
            return true;
        }

        if (TryGetTrustedNewServerCardState(
                solverState,
                pendingDirectionHasBaselineServerRowSequence,
                pendingDirectionBaselineServerRowSequence,
                requireDirectionReveal: true,
                out var rowSequence))
        {
            ConfirmPendingDirection(state, pending, now, rowSequence);
            return true;
        }

        if (IsPendingDirectionTimedOut(now))
        {
            TimeoutPendingDirection(state, solverState, surface, now, pending);
            return true;
        }

        if (TryRetryPendingDirectionInteract(state, pending, now))
            return true;

        Status = BuildStatus(state, $"waiting: direction proof {pendingDirectionName}");
        return true;
    }

    private bool TryRetryPendingDirectionInteract(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        DateTime now)
    {
        if (pendingDirectionPhase != PendingDirectionPhase.WaitingSelectYesno)
            return false;

        var target = FindPendingDirectionRetryTarget(pendingDirectionName);
        float? distance = target is null ? null : DistanceToLocalPlayer(target);
        lastDirectionTargetName = string.IsNullOrWhiteSpace(pendingDirectionName) ? "none" : pendingDirectionName;
        lastDirectionTargetDistance = distance.HasValue && float.IsFinite(distance.Value) ? distance.Value : null;

        var retryDecision = HigherLowerPendingDirectionRetryPolicy.Evaluate(new HigherLowerPendingDirectionRetryPolicy.RetryState(
            WaitingSelectYesno: pendingDirectionPhase == PendingDirectionPhase.WaitingSelectYesno,
            HigherLowerPromptVisible: state.SelectYesnoVisible && IsHigherLowerPrompt(state.SelectYesnoPrompt),
            TrustedServerProofAvailable: false,
            PendingTimedOut: IsPendingDirectionTimedOut(now),
            PendingStartedUtc: pendingDirectionStartedUtc,
            NextRetryUtc: pendingDirectionNextRetryUtc,
            InteractAttempts: pendingDirectionInteractAttempts,
            TargetAvailable: target is not null,
            TargetDistance: distance,
            InteractRange: DirectionInteractRange,
            NowUtc: now));

        if (!retryDecision.ShouldRetry)
        {
            if (now >= retryDecision.NextRetryUtc && IsPendingDirectionRetryTargetGateReason(retryDecision.Reason))
            {
                SchedulePendingDirectionRetry(now, retryDecision.Reason);
                var attempt = pendingDirectionInteractAttempts + 1;
                Status = BuildStatus(state, $"waiting: direction retry {retryDecision.Reason} {pendingDirectionName}");
                LogWarning($"hlauto direction-retry-target-missing target={EscapeToken(pendingDirectionName)} reason={EscapeToken(retryDecision.Reason)} {BuildRetryTargetLogFields(target, distance)} {BuildPendingDirectionRetryLogFields(attempt, now)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "pending-target")} {BuildPendingDirectionLogFields()}");
                return true;
            }

            return false;
        }

        var retryTarget = target;
        if (retryTarget is null || !distance.HasValue || !float.IsFinite(distance.Value))
            return false;

        var nextAttempt = pendingDirectionInteractAttempts + 1;
        TryStopVnav($"direction-retry-{pendingDirectionName}", force: true);
        if (GameInteractionHelper.TryInteractWithObject(targetManager, retryTarget, log))
        {
            RecordPendingDirectionRetryAttempt(nextAttempt, now, "interact-sent");
            MarkHigherLowerActivity(now, $"hlauto-retry-interact:{pendingDirectionName}");
            Status = BuildStatus(state, $"direction-retry-interact-sent {pendingDirectionName} attempt {nextAttempt}/{HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts}");
            LogAction($"hlauto direction-retry-interact-sent target={EscapeToken(pendingDirectionName)} {BuildRetryTargetLogFields(retryTarget, distance)} {BuildPendingDirectionRetryLogFields(nextAttempt, now)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "pending-target")} {BuildPendingDirectionLogFields()}");
            return true;
        }

        RecordPendingDirectionRetryAttempt(nextAttempt, now, "interact-failed");
        Status = BuildStatus(state, $"waiting: direction retry interact failed {pendingDirectionName} attempt {nextAttempt}/{HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts}");
        LogWarning($"hlauto direction-retry-interact-failed target={EscapeToken(pendingDirectionName)} {BuildRetryTargetLogFields(retryTarget, distance)} {BuildPendingDirectionRetryLogFields(nextAttempt, now)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "pending-target")} {BuildPendingDirectionLogFields()}");
        return true;
    }

    private void RecordPendingDirectionRetryAttempt(int attempt, DateTime now, string reason)
    {
        pendingDirectionInteractAttempts = Math.Max(pendingDirectionInteractAttempts, attempt);
        pendingDirectionLastInteractUtc = now;
        pendingDirectionNextRetryUtc = HigherLowerPendingDirectionRetryPolicy.GetNextRetryUtc(now);
        pendingDirectionLastRetryReason = reason;
    }

    private void SchedulePendingDirectionRetry(DateTime now, string reason)
    {
        pendingDirectionNextRetryUtc = HigherLowerPendingDirectionRetryPolicy.GetNextRetryUtc(now);
        pendingDirectionLastRetryReason = reason;
    }

    private static bool IsPendingDirectionRetryTargetGateReason(string reason)
        => string.Equals(reason, "target-missing", StringComparison.Ordinal)
           || string.Equals(reason, "target-distance-unavailable", StringComparison.Ordinal)
           || string.Equals(reason, "target-out-of-range", StringComparison.Ordinal);

    private void TryAnswerPendingDirectionConfirm(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        DateTime now)
    {
        if (now < nextPromptAnswerUtc)
        {
            Status = BuildStatus(state, "waiting: direction confirm prompt retry cooldown");
            return;
        }

        if (!TryResolveDirectionPromptAnswer(state.SelectYesnoPrompt, decision.Action, out var yes, out var promptLabel))
        {
            Status = BuildStatus(state, $"blocked: unrelated SelectYesno prompt '{state.SelectYesnoPrompt}'");
            return;
        }

        nextPromptAnswerUtc = now + PromptAnswerRetryCooldown;
        TryStopVnav("direction-confirm", force: true);
        if (GameInteractionHelper.TrySelectYesNo(yes, gameGui, log))
        {
            pendingDirectionPhase = PendingDirectionPhase.WaitingProof;
            pendingDirectionPhaseStartedUtc = now;
            MarkHigherLowerActivity(now, "hlauto-direction-confirm");
            Status = BuildStatus(state, $"direction confirm answered {pendingDirectionName}; waiting proof");
            LogAction($"hlauto direction-confirm-answered action={decision.Action} answer={(yes ? "Yes" : "No")} prompt='{Escape(promptLabel)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "pending-target")} {BuildPendingDirectionLogFields()}");
            return;
        }

        Status = BuildStatus(state, $"blocked: direction confirm failed {pendingDirectionName}");
        LogWarning($"hlauto direction-confirm-answer-failed action={decision.Action} answer={(yes ? "Yes" : "No")} prompt='{Escape(promptLabel)}' {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "pending-target")} {BuildPendingDirectionLogFields()}");
    }

    private void ConfirmPendingDirection(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        DateTime now,
        ulong rowSequence)
    {
        LogAction($"hlauto direction-confirmed action={decision.Action} proof=server-right-reveal rowSeq={rowSequence.ToString(CultureInfo.InvariantCulture)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "direction-proof")} {BuildPendingDirectionLogFields()}");
        Status = BuildStatus(state, $"direction-confirmed {pendingDirectionName}: proof=server-right-reveal");
        CommitPlay(decision, "server-right-reveal");
    }

    private void TimeoutPendingDirection(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        DateTime now,
        AutomationDecision decision)
    {
        pendingDirectionTimedOutPhase = pendingDirectionPhase;
        pendingDirectionPhase = PendingDirectionPhase.TimedOut;
        if (!pendingDirectionTimeoutLogged)
        {
            pendingDirectionTimeoutLogged = true;
            LogWarning($"hlauto direction-timeout action={decision.Action} phase={FormatPendingDirectionPhase(pendingDirectionTimedOutPhase)} actionableSurface={surface.Present.ToString().ToLowerInvariant()} surface={EscapeToken(surface.Source)} age={FormatPendingDirectionPhaseAgeSeconds(now)} {BuildPendingDirectionLogFields()}");
        }

        if (!surface.Present)
        {
            CompletePendingDirectionTerminal(state, decision, "surface-cleared-stale-objects", now);
            return;
        }

        BlockPendingDirectionTimeout(state, solverState, surface, decision);
    }

    private void BlockPendingDirectionTimeout(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerSurface surface,
        AutomationDecision decision)
    {
        Block(
            state,
            solverState,
            surface,
            "blocked: direction proof timeout",
            $"direction {decision.Action} timed out while {FormatPendingDirectionPhase(pendingDirectionTimedOutPhase == PendingDirectionPhase.None ? pendingDirectionPhase : pendingDirectionTimedOutPhase)}; actionableSurface={surface.Source}",
            incompleteAddon: true);
    }

    private void CompletePendingDirectionTerminal(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        AutomationDecision decision,
        string proof,
        DateTime now)
    {
        LogAction($"hlauto direction-terminal action={decision.Action} proof={EscapeToken(proof)} {BuildAddonLogFields(state)} {BuildDecisionLogFields(decision, now, "direction-terminal")} {BuildPendingDirectionLogFields()}");
        retainedDecision = null;
        ClearDirectionState(clearTarget: true, clearTerminalProof: false);
        ClearPendingCallbackState();
        ClearHigherLowerActivitySession();
        lastDirectionTerminalProof = proof;
        lastBlockedReason = string.Empty;
        lastDirectionDecisionSource = "none";
        Status = BuildStatus(state, $"direction-terminal proof={proof}");
    }

    private void CompleteHigherLowerTerminalFromChat(string proof, DateTime now)
    {
        var state = diagnostics.CaptureRuntimeState();
        var decision = pendingDirectionDecision
                       ?? retainedDecision
                       ?? pendingCallbackDecision
                       ?? lastDecision;

        if (decision is not null)
        {
            CompletePendingDirectionTerminal(state, decision, proof, now);
            return;
        }

        LogAction($"hlauto direction-terminal action=None proof={EscapeToken(proof)} {BuildAddonLogFields(state)} {BuildSessionLogFields()}");
        retainedDecision = null;
        ClearDirectionState(clearTarget: true, clearTerminalProof: false);
        ClearPendingCallbackState();
        ClearHigherLowerActivitySession();
        lastDirectionTerminalProof = proof;
        lastBlockedReason = string.Empty;
        lastDirectionDecisionSource = "none";
        Status = BuildStatus(state, $"direction-terminal proof={proof}");
    }

    private bool HasHigherLowerTerminalContext(DateTime now)
        => pendingDirectionDecision is not null
           || pendingCallbackDecision is not null
           || retainedDecision is not null
           || IsDutyExitGraceActive(now);

    private void ClearDirectionState(bool clearTarget, bool clearTerminalProof = true)
    {
        pendingDirectionDecision = null;
        pendingDirectionName = string.Empty;
        pendingDirectionGameObjectId = 0;
        pendingDirectionStartedUtc = DateTime.MinValue;
        pendingDirectionPhaseStartedUtc = DateTime.MinValue;
        pendingDirectionPhase = PendingDirectionPhase.None;
        pendingDirectionTimedOutPhase = PendingDirectionPhase.None;
        pendingDirectionBaselineServerRowSequence = 0;
        pendingDirectionHasBaselineServerRowSequence = false;
        pendingDirectionTimeoutLogged = false;
        pendingDirectionInteractAttempts = 0;
        pendingDirectionLastInteractUtc = DateTime.MinValue;
        pendingDirectionNextRetryUtc = DateTime.MinValue;
        pendingDirectionLastRetryReason = "none";
        nextStopCommandUtc = DateTime.MinValue;
        lastStopLogKey = string.Empty;
        if (!clearTarget)
            return;

        lastDirectionTargetName = "none";
        lastDirectionTargetDistance = null;
        if (clearTerminalProof)
            lastDirectionTerminalProof = "none";
    }

    private void ClearPendingCallbackState()
    {
        pendingCallbackAction = PendingCallbackAction.None;
        pendingCallbackPhase = PendingCallbackPhase.None;
        pendingCallbackTimedOutPhase = PendingCallbackPhase.None;
        pendingCallbackDecision = null;
        pendingCallbackSentUtc = DateTime.MinValue;
        pendingCallbackPhaseStartedUtc = DateTime.MinValue;
        pendingCallbackBaselineServerRowSequence = 0;
        pendingCallbackHasBaselineServerRowSequence = false;
        pendingCallbackConfirmVisibleLogged = false;
        pendingCallbackTimeoutLogged = false;
    }

    private bool HasPendingCallback()
        => pendingCallbackAction != PendingCallbackAction.None
           && pendingCallbackPhase != PendingCallbackPhase.None;

    private bool IsPendingOpenChestCallback()
        => pendingCallbackAction == PendingCallbackAction.OpenChest
           && pendingCallbackPhase != PendingCallbackPhase.None;

    private bool IsPendingCallbackTimedOut(DateTime now)
        => pendingCallbackPhaseStartedUtc != DateTime.MinValue
           && now - pendingCallbackPhaseStartedUtc >= PendingCallbackTimeout;

    private bool TryGetPendingCallbackProof(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        out string proof,
        out ulong? serverRowSequence)
    {
        proof = string.Empty;
        serverRowSequence = null;
        if (pendingCallbackAction == PendingCallbackAction.TryLuck)
        {
            if (state.SelectYesnoVisible && IsHigherLowerPrompt(state.SelectYesnoPrompt))
            {
                proof = "selectyesno-highlow-prompt";
                return true;
            }

            if (state.HighTargetable || state.LowTargetable)
            {
                proof = "targetable-highlow";
                return true;
            }

            if (TryGetTrustedNewServerCardState(
                    solverState,
                    pendingCallbackHasBaselineServerRowSequence,
                    pendingCallbackBaselineServerRowSequence,
                    requireDirectionReveal: false,
                    out var rowSequence))
            {
                proof = "trusted-server-card-state";
                serverRowSequence = rowSequence;
                return true;
            }

            return false;
        }

        if (pendingCallbackAction == PendingCallbackAction.OpenChest)
        {
            if (!state.TreasureHighLowVisible
                && !state.NotificationChallengeVisible
                && !state.HighTargetable
                && !state.LowTargetable
                && !state.SelectYesnoVisible)
            {
                proof = "higherlower-surface-cleared";
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryGetTrustedNewServerCardState(
        HigherLowerCardVfxSolverService.SolverState solverState,
        bool hasBaselineServerRowSequence,
        ulong baselineServerRowSequence,
        bool requireDirectionReveal,
        out ulong rowSequence)
    {
        rowSequence = 0;
        if (solverState.Confidence != HigherLowerCardVfxSolverService.SolverConfidence.High
            || !solverState.CurrentCard.HasValue
            || solverState.CurrentCard.Value is < 1 or > 9
            || (requireDirectionReveal && !IsDirectionRevealSolverState(solverState))
            || !TryGetServerRowSequence(solverState, out rowSequence))
        {
            return false;
        }

        return !hasBaselineServerRowSequence || rowSequence > baselineServerRowSequence;
    }

    private void CaptureServerRowBaseline(
        HigherLowerCardVfxSolverService.SolverState solverState,
        out ulong baselineServerRowSequence,
        out bool hasBaselineServerRowSequence)
    {
        baselineServerRowSequence = 0;
        hasBaselineServerRowSequence = false;

        if (TryGetServerRowSequence(solverState, out var sourceRowSequence))
        {
            baselineServerRowSequence = sourceRowSequence;
            hasBaselineServerRowSequence = true;
        }

        if (!solver.TryGetLatestServerRowSequence(out var latestRowSequence))
            return;

        baselineServerRowSequence = hasBaselineServerRowSequence
            ? Math.Max(baselineServerRowSequence, latestRowSequence)
            : latestRowSequence;
        hasBaselineServerRowSequence = true;
    }

    private static bool IsDirectionRevealSolverState(HigherLowerCardVfxSolverService.SolverState solverState)
        => IsPostSelectionRevealSolverState(solverState);

    private static bool TryGetServerRowSequence(
        HigherLowerCardVfxSolverService.SolverState solverState,
        out ulong rowSequence)
    {
        rowSequence = 0;
        if (!IsServerSolverState(solverState)
            || !solverState.SourceRowSequence.HasValue)
        {
            return false;
        }

        rowSequence = solverState.SourceRowSequence.Value;
        return true;
    }

    private static bool IsServerSolverState(HigherLowerCardVfxSolverService.SolverState solverState)
        => string.Equals(solverState.CardSource, HigherLowerCardVfxSolverService.ServerEObjAnimSource, StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectionProof(string proof)
        => string.Equals(proof, "selectyesno-highlow-prompt", StringComparison.OrdinalIgnoreCase)
           || string.Equals(proof, "targetable-highlow", StringComparison.OrdinalIgnoreCase);

    private string BuildPendingCallbackTimeoutReason()
    {
        var phase = pendingCallbackTimedOutPhase == PendingCallbackPhase.None
            ? pendingCallbackPhase
            : pendingCallbackTimedOutPhase;
        return $"callback {FormatPendingCallbackAction(pendingCallbackAction)} timed out while {FormatPendingCallbackPhase(phase)}";
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

    private static bool TryResolveDirectionPromptAnswer(
        string prompt,
        HigherLowerAction action,
        out bool yes,
        out string promptLabel)
    {
        yes = false;
        promptLabel = string.Empty;
        if (prompt.Contains("Guess higher?", StringComparison.OrdinalIgnoreCase))
        {
            promptLabel = "Guess higher?";
            yes = action == HigherLowerAction.PlayHigh;
            return true;
        }

        if (prompt.Contains("Guess lower?", StringComparison.OrdinalIgnoreCase))
        {
            promptLabel = "Guess lower?";
            yes = action == HigherLowerAction.PlayLow;
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanStartOpeningPlay(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        string blockedReason)
        => currentGambleStep == 1
           && completedPlayCount <= 0
           && state.TreasureHighLowVisible
           && (IsBlankAddonCard(state)
               || blockedReason.Contains("no trusted runtime card decode", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlankAddonCard(TreasureHighLowDiagnosticService.HigherLowerRuntimeState state)
        => string.Equals(state.AddonCurrentCardText.Trim(), "blank", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayAction(HigherLowerAction action)
        => action is HigherLowerAction.PlayHigh or HigherLowerAction.PlayLow;

    private static string DirectionNameForAction(HigherLowerAction action)
        => action == HigherLowerAction.PlayHigh ? "High" : "Low";

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
        => $"Higher/Lower automation {suffix}; surface={lastSurfaceSource} dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} card={FormatCard(lastDecision?.Card)} action={lastDecision?.Action.ToString() ?? "None"} source='{lastDecision?.Source ?? "none"}' directionSource={lastDirectionDecisionSource} directionTarget={lastDirectionTargetName}@{FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : pendingDirectionName)} pendingPhase={FormatPendingDirectionPhase(pendingDirectionPhase)} pendingAge={FormatPendingAgeSeconds()} pendingPhaseAge={FormatPendingDirectionPhaseAgeSeconds(DateTime.UtcNow)} {BuildPendingDirectionRetryStatusFields(DateTime.UtcNow)} pendingBaselineServerRowSeq={FormatPendingDirectionBaselineServerRowSequence()} terminalProof={EscapeToken(lastDirectionTerminalProof)} callbackAction={FormatPendingCallbackAction(pendingCallbackAction)} callbackPhase={FormatPendingCallbackPhase(pendingCallbackPhase)} callbackAge={FormatPendingCallbackAgeSeconds(DateTime.UtcNow)} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} blockedReason='{lastBlockedReason}' active={state.Active} addonCurrentCard={state.AddonCurrentCardText} addonOtherCard={state.AddonOtherCardText} knownCards={state.KnownCardCount}.";

    private string BuildBlockedStatus(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState state,
        HigherLowerCardVfxSolverService.SolverState solverState,
        string suffix,
        string blockedReason)
        => $"Higher/Lower solver {suffix}; surface={lastSurfaceSource} dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} directionSource={lastDirectionDecisionSource} directionTarget={lastDirectionTargetName}@{FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : pendingDirectionName)} pendingPhase={FormatPendingDirectionPhase(pendingDirectionPhase)} pendingAge={FormatPendingAgeSeconds()} pendingPhaseAge={FormatPendingDirectionPhaseAgeSeconds(DateTime.UtcNow)} {BuildPendingDirectionRetryStatusFields(DateTime.UtcNow)} pendingBaselineServerRowSeq={FormatPendingDirectionBaselineServerRowSequence()} terminalProof={EscapeToken(lastDirectionTerminalProof)} callbackAction={FormatPendingCallbackAction(pendingCallbackAction)} callbackPhase={FormatPendingCallbackPhase(pendingCallbackPhase)} callbackAge={FormatPendingCallbackAgeSeconds(DateTime.UtcNow)} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} blockedReason='{blockedReason}' active={state.Active} addonCurrentCard={state.AddonCurrentCardText} addonOtherCard={state.AddonOtherCardText} decodedCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} solverChoice={solverState.RecommendedChoice} confidence={solverState.Confidence.ToString().ToLowerInvariant()} reason='{solverState.Reason}' source='{solverState.CardSource}' slot={solverState.Slot} textureIndex={(solverState.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}.";

    private string BuildSessionLogFields()
        => $"dutyKey={activeDutyKey} step={currentGambleStep} playsCompleted={completedPlayCount} directionSource={EscapeToken(lastDirectionDecisionSource)} directionTarget={EscapeToken(lastDirectionTargetName)} directionDistance={FormatDistance(lastDirectionTargetDistance)} pendingTarget={(pendingDirectionDecision is null ? "none" : EscapeToken(pendingDirectionName))} pendingPhase={FormatPendingDirectionPhase(pendingDirectionPhase)} pendingAge={FormatPendingAgeSeconds()} pendingPhaseAge={FormatPendingDirectionPhaseAgeSeconds(DateTime.UtcNow)} {BuildPendingDirectionRetryStatusFields(DateTime.UtcNow)} pendingBaselineServerRowSeq={FormatPendingDirectionBaselineServerRowSequence()} terminalProof={EscapeToken(lastDirectionTerminalProof)} callbackAction={FormatPendingCallbackAction(pendingCallbackAction)} callbackPhase={FormatPendingCallbackPhase(pendingCallbackPhase)} callbackAge={FormatPendingCallbackAgeSeconds(DateTime.UtcNow)} blocksDutyExit={BlocksDutyExit.ToString().ToLowerInvariant()} exitGrace={FormatDutyExitGraceRemainingSeconds()} lastActivity={EscapeToken(lastHigherLowerActivitySource)} retained={(retainedDecision is not null).ToString().ToLowerInvariant()} retainedStep={(retainedDecision?.Step.ToString(CultureInfo.InvariantCulture) ?? "none")} retainedCard={FormatCard(retainedDecision?.Card)} retainedAction={(retainedDecision?.Action.ToString() ?? "None")}";

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

    private static string BuildRetryTargetLogFields(IGameObject? target, float? distance)
    {
        var targetId = target is null
            ? "none"
            : string.Create(CultureInfo.InvariantCulture, $"0x{target.GameObjectId:X}");
        return $"targetId={targetId} distance={FormatDistance(distance)} range={DirectionInteractRange.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    private string BuildPendingDirectionRetryLogFields(int attempt, DateTime now)
        => $"attempt={attempt.ToString(CultureInfo.InvariantCulture)} maxAttempts={HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts.ToString(CultureInfo.InvariantCulture)} pendingAge={FormatPendingAgeSeconds(now)} phaseAge={FormatPendingDirectionPhaseAgeSeconds(now)} nextRetryIn={FormatPendingDirectionNextRetryInSeconds(now)} nextRetryAt={FormatPendingDirectionNextRetryUtc()} lastRetryReason={EscapeToken(pendingDirectionLastRetryReason)}";

    private float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = objectTable.LocalPlayer;
        return player == null ? float.NaN : Vector3.Distance(player.Position, obj.Position);
    }

    private double? GetPendingDirectionAgeSeconds(DateTime now)
        => pendingDirectionDecision is null || pendingDirectionStartedUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (now - pendingDirectionStartedUtc).TotalSeconds);

    private double? GetPendingDirectionPhaseAgeSeconds(DateTime now)
        => pendingDirectionDecision is null || pendingDirectionPhaseStartedUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (now - pendingDirectionPhaseStartedUtc).TotalSeconds);

    private double? GetPendingDirectionLastInteractAgeSeconds(DateTime now)
        => pendingDirectionDecision is null || pendingDirectionLastInteractUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (now - pendingDirectionLastInteractUtc).TotalSeconds);

    private double? GetPendingDirectionNextRetryInSeconds(DateTime now)
        => pendingDirectionDecision is null || pendingDirectionNextRetryUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (pendingDirectionNextRetryUtc - now).TotalSeconds);

    private double? GetPendingCallbackAgeSeconds(DateTime now)
        => !HasPendingCallback() || pendingCallbackPhaseStartedUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (now - pendingCallbackPhaseStartedUtc).TotalSeconds);

    private bool IsPendingDirectionTimedOut(DateTime now)
        => pendingDirectionPhaseStartedUtc != DateTime.MinValue
           && now - pendingDirectionPhaseStartedUtc >= PendingDirectionTimeout;

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
        => FormatPendingAgeSeconds(DateTime.UtcNow);

    private string FormatPendingAgeSeconds(DateTime now)
    {
        var age = GetPendingDirectionAgeSeconds(now);
        return age.HasValue
            ? age.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string FormatPendingDirectionPhaseAgeSeconds(DateTime now)
    {
        var age = GetPendingDirectionPhaseAgeSeconds(now);
        return age.HasValue
            ? age.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string FormatPendingDirectionLastInteractAgeSeconds(DateTime now)
    {
        var age = GetPendingDirectionLastInteractAgeSeconds(now);
        return age.HasValue
            ? age.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string FormatPendingDirectionNextRetryInSeconds(DateTime now)
    {
        var remaining = GetPendingDirectionNextRetryInSeconds(now);
        return remaining.HasValue
            ? remaining.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string FormatPendingDirectionNextRetryUtc()
        => pendingDirectionNextRetryUtc == DateTime.MinValue
            ? "none"
            : pendingDirectionNextRetryUtc.ToString("O", CultureInfo.InvariantCulture);

    private string FormatPendingCallbackAgeSeconds(DateTime now)
    {
        var age = GetPendingCallbackAgeSeconds(now);
        return age.HasValue
            ? age.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }

    private string BuildPendingCallbackLogFields()
        => $"callbackAction={FormatPendingCallbackAction(pendingCallbackAction)} callbackPhase={FormatPendingCallbackPhase(pendingCallbackPhase)} callbackAge={FormatPendingCallbackAgeSeconds(DateTime.UtcNow)} callbackSentAge={FormatPendingCallbackSentAgeSeconds(DateTime.UtcNow)} callbackBaselineServerRowSeq={(pendingCallbackHasBaselineServerRowSequence ? pendingCallbackBaselineServerRowSequence.ToString(CultureInfo.InvariantCulture) : "none")}";

    private string BuildPendingDirectionLogFields()
    {
        var now = DateTime.UtcNow;
        return $"directionPhase={FormatPendingDirectionPhase(pendingDirectionPhase)} directionAge={FormatPendingAgeSeconds(now)} directionPhaseAge={FormatPendingDirectionPhaseAgeSeconds(now)} {BuildPendingDirectionRetryStatusFields(now)} directionBaselineServerRowSeq={FormatPendingDirectionBaselineServerRowSequence()} directionTerminalProof={EscapeToken(lastDirectionTerminalProof)}";
    }

    private string BuildPendingDirectionRetryStatusFields(DateTime now)
        => $"directionAttempts={pendingDirectionInteractAttempts.ToString(CultureInfo.InvariantCulture)}/{HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts.ToString(CultureInfo.InvariantCulture)} directionLastInteractAge={FormatPendingDirectionLastInteractAgeSeconds(now)} directionNextRetryIn={FormatPendingDirectionNextRetryInSeconds(now)} directionNextRetryAt={FormatPendingDirectionNextRetryUtc()} directionRetryReason={EscapeToken(pendingDirectionLastRetryReason)}";

    private string FormatPendingDirectionBaselineServerRowSequence()
        => pendingDirectionHasBaselineServerRowSequence
            ? pendingDirectionBaselineServerRowSequence.ToString(CultureInfo.InvariantCulture)
            : "none";

    private string FormatPendingCallbackSentAgeSeconds(DateTime now)
    {
        if (!HasPendingCallback() || pendingCallbackSentUtc == DateTime.MinValue)
            return "none";

        return Math.Max(0, (now - pendingCallbackSentUtc).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string FormatPendingCallbackAction(PendingCallbackAction action)
        => action switch
        {
            PendingCallbackAction.TryLuck => "TryLuck",
            PendingCallbackAction.OpenChest => "OpenChest",
            _ => "none",
        };

    private static string FormatPendingCallbackPhase(PendingCallbackPhase phase)
        => phase switch
        {
            PendingCallbackPhase.WaitingSurface => "waiting-surface",
            PendingCallbackPhase.WaitingSelectYesno => "waiting-selectyesno",
            PendingCallbackPhase.WaitingProof => "waiting-proof",
            PendingCallbackPhase.TimedOut => "timed-out",
            _ => "none",
        };

    private static HigherLowerCashOutRecoveryPolicy.PendingPhase ToCashOutRecoveryPendingPhase(PendingCallbackPhase phase)
        => phase switch
        {
            PendingCallbackPhase.WaitingSurface => HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSurface,
            PendingCallbackPhase.WaitingSelectYesno => HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSelectYesno,
            PendingCallbackPhase.WaitingProof => HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingProof,
            PendingCallbackPhase.TimedOut => HigherLowerCashOutRecoveryPolicy.PendingPhase.TimedOut,
            _ => HigherLowerCashOutRecoveryPolicy.PendingPhase.None,
        };

    private static string FormatPendingDirectionPhase(PendingDirectionPhase phase)
        => phase switch
        {
            PendingDirectionPhase.WaitingSelectYesno => "waiting-selectyesno",
            PendingDirectionPhase.WaitingProof => "waiting-proof",
            PendingDirectionPhase.TimedOut => "timed-out",
            _ => "none",
        };

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

    private enum PendingCallbackAction
    {
        None = 0,
        TryLuck = 1,
        OpenChest = 2,
    }

    private enum PendingCallbackPhase
    {
        None = 0,
        WaitingSurface = 1,
        WaitingSelectYesno = 2,
        WaitingProof = 3,
        TimedOut = 4,
    }

    private enum PendingDirectionPhase
    {
        None = 0,
        WaitingSelectYesno = 1,
        WaitingProof = 2,
        TimedOut = 3,
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
        string PendingDirectionPhase,
        double? PendingDirectionAgeSeconds,
        double? PendingDirectionPhaseAgeSeconds,
        int PendingDirectionInteractAttempts,
        int PendingDirectionMaxInteractAttempts,
        DateTime? PendingDirectionLastInteractUtc,
        double? PendingDirectionLastInteractAgeSeconds,
        DateTime? PendingDirectionNextRetryUtc,
        double? PendingDirectionNextRetryInSeconds,
        string PendingDirectionLastRetryReason,
        ulong? PendingDirectionBaselineServerRowSequence,
        string PendingDirectionTerminalProof,
        string PendingCallbackAction,
        string PendingCallbackPhase,
        double? PendingCallbackAgeSeconds,
        bool BlocksDutyExit,
        DateTime LastHigherLowerActivityUtc,
        string LastHigherLowerActivitySource,
        double? DutyExitGraceRemainingSeconds);
}
