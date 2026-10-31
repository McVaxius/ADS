using ADS.Models;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private RaidDutyPolicy? raidPolicy;
    internal RaidDutyPolicy RaidPolicy => raidPolicy ??= new RaidDutyPolicy();
    private string? raidLeaveSessionId;
    private string? observedRaidSessionId;
    private string? raidOwnershipSessionId;
    private long raidOwnershipRecoveryGeneration;
    private bool raidOwnershipIsManual;
    private bool raidProgressionHeld;
    private (string? Session, uint Territory, uint Duty, long Revision, bool NeedsBaseline)? raidCountdownWatermark;
    internal Func<bool?>? QueryRaidPlayerDead { get; set; }
    internal Func<bool?>? QueryRaidOutstandingLoot { get; set; }

    internal RaidDutyState ReadRaidState(DutyContextSnapshot context)
    {
        var raid = RaidPolicy.Read(context);
        ObserveRaidCountdownWatermark(context, raid);
        if (!raid.Enabled)
            observedRaidSessionId = null;
        else if (raid.Matches(context) && observedRaidSessionId != (raid.SessionId ?? string.Empty))
        {
            observedRaidSessionId = raid.SessionId ?? string.Empty;
            // Older FR automatic `/ads leave` requests also used the explicit
            // flag. Cancel every inherited staged exit at activation; a new
            // deliberate leave observes this boundary before staging its work.
            if (CurrentMode == OwnershipMode.Leaving)
                Stop(context, "New Raid session cancelled the previous staged duty exit.");
            else if (IsActiveOwnedDutyMode())
                CaptureRaidOwnership(raid, manual: false);
        }
        return raid;
    }

    internal void InvalidateRaidCountdownForOperator(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        if (!context.IsLoggedIn || !context.InInstancedDuty && !context.IsUnsafeTransition
            || !raid.Enabled && raid.Readable)
        {
            raidCountdownWatermark = null;
            return;
        }

        var readable = raid.Matches(context) && raid.CountdownRevision.HasValue;
        var previous = raidCountdownWatermark;
        raidCountdownWatermark = (readable ? raid.SessionId : null,
            context.TerritoryTypeId != 0 ? context.TerritoryTypeId : raid.TerritoryTypeId,
            context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : raid.ContentFinderConditionId,
            readable ? Math.Max(previous?.Revision ?? 0, raid.CountdownRevision!.Value) : 0, !readable);
    }

    private void ObserveRaidCountdownWatermark(DutyContextSnapshot context, RaidDutyState raid)
    {
        if (raidCountdownWatermark is not { } watermark)
            return;
        if (!context.IsLoggedIn || !context.IsUnsafeTransition
            && (!context.InInstancedDuty
                || watermark.Territory != 0 && watermark.Territory != context.TerritoryTypeId
                || watermark.Duty != 0 && watermark.Duty != context.ContentFinderConditionId)
            || !raid.Enabled && raid.Readable
            || raid.Matches(context) && watermark.Session is not null && raid.SessionId is not null
                && watermark.Session != raid.SessionId)
        {
            raidCountdownWatermark = null;
            return;
        }
        if (watermark.NeedsBaseline && raid.Matches(context) && raid.CountdownRevision is { } revision)
        {
            // An unreadable operator boundary cannot establish which timer was
            // current. Withhold the first readable revision; only a later new
            // countdown can authorize automation.
            raidCountdownWatermark = (raid.SessionId, context.TerritoryTypeId, context.ContentFinderConditionId,
                Math.Max(watermark.Revision, revision), false);
        }
    }

    private bool IsRaidCountdownGrantAllowed(DutyContextSnapshot context, RaidDutyState raid)
        => raid.Matches(context) && raid.AutomaticStartAllowedNow && raid.CountdownRevision is { } revision
           && (raidCountdownWatermark is not { } watermark
               || !watermark.NeedsBaseline && revision > watermark.Revision);

    private void CaptureRaidOwnership(RaidDutyState raid, bool manual)
    {
        raidOwnershipSessionId = raid.Enabled ? raid.SessionId : null;
        raidOwnershipRecoveryGeneration = raid.RecoveryGeneration;
        raidOwnershipIsManual = manual && raid.Readable;
        raidProgressionHeld = false;
    }

    private void ClearRaidOwnership()
    {
        raidOwnershipSessionId = null;
        raidOwnershipIsManual = false;
        raidProgressionHeld = false;
    }

    internal bool AreTreasureCoffersEnabled(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        return raid.Enabled ? raid.Matches(context) && raid.AdsLootEnabled : configuration.ConsiderTreasureCoffers;
    }

    internal LootRollMode GetEffectiveLootMode(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        if (!raid.Enabled)
            return configuration.LootMode;
        if (!raid.Matches(context) || !raid.AdsLootEnabled)
            return LootRollMode.Off;
        // The single Raid toggle enables the normal Need policy when ordinary
        // loot is Off, retaining native eligibility/fallback rules and saved preferences.
        return configuration.LootMode == LootRollMode.Off ? LootRollMode.Need : configuration.LootMode;
    }

    internal bool SuppressRaidRecoveryDialogs(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        return raid.Enabled && (!raid.Readable || ReadRaidPlayerDead() != false);
    }

    internal bool CanStartRaidDuty(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        return IsRaidCountdownGrantAllowed(context, raid)
               && !context.IsUnsafeTransition && !context.IsCutscene
               && completedDuty != (context.TerritoryTypeId, context.ContentFinderConditionId)
               && ReadRaidPlayerDead() == false && GetStartBlocker(context, fromOutside: false) is null;
    }

    internal bool StartRaidDutyFromInside(DutyContextSnapshot context)
    {
        if (!CanStartRaidDuty(context))
            return false;
        var raid = ReadRaidState(context);
        if (!IsRaidCountdownGrantAllowed(context, raid))
            return false;
        // Reserve before resets or synchronous callbacks so one grant cannot
        // restart execution twice or replace newer explicit operator intent.
        raidCountdownWatermark = (raid.SessionId, context.TerritoryTypeId, context.ContentFinderConditionId,
            raid.CountdownRevision!.Value, false);
        if (!StartDutyFromInside(context))
            return false;
        CaptureRaidOwnership(raid, manual: false);
        return true;
    }

    internal bool RefreshRaidProgressionHold(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        if (raid.Enabled && CurrentMode == OwnershipMode.OwnedStartOutside)
        {
            Stop(context, "Raid mode cancelled the previous queued outside start; waiting for a fresh pull countdown.");
            return true;
        }
        if (!IsActiveOwnedDutyMode())
        {
            raidProgressionHeld = false;
            return false;
        }
        if (!raid.Enabled)
        {
            if (raidOwnershipSessionId is not null && !raidOwnershipIsManual)
            {
                Stop(context, "Raid progression cancelled; waiting for an explicit start or fresh countdown.");
                return true;
            }
            ClearRaidOwnership();
            return false;
        }
        if (!context.InInstancedDuty && !context.IsUnsafeTransition)
        {
            ClearRaidOwnership();
            return false;
        }
        if (raidOwnershipSessionId != raid.SessionId)
            CaptureRaidOwnership(raid, manual: false);
        var hold = !raid.Readable || raid.RecoveryGeneration != raidOwnershipRecoveryGeneration
                   || !raidOwnershipIsManual && raid.AwaitingCountdown;
        if (hold && !raidProgressionHeld)
        {
            CancelDebugReturn();
            CancelInteractionPause(context, allowRestore: true);
            ReleaseHyperFocus("Raid progression hold");
            ReleaseCrystalTowerMovement(context, restore: true);
            InterruptCardinalHold("Raid progression hold");
            StopMovementAssists();
            ClearInteractableCommitment();
            ClearCommittedForceMarchManualDestination();
        }
        raidProgressionHeld = hold;
        if (hold)
            SetPhase(ExecutionPhase.WaitingForTruth, "Raid progression is waiting for a fresh pull countdown.");
        return hold;
    }

    internal bool LeaveRaidDuty(DutyContextSnapshot context)
    {
        var raid = ReadRaidState(context);
        if (!CanDispatchRaidLeave(context, raid))
            return false;
        if (raidLeaveSessionId == raid.SessionId && IsLeaveRequested)
            return true;
        return BeginLeaveDuty(context, considerTreasureCoffers: false, raid.SessionId);
    }

    private bool? ReadRaidPlayerDead()
    {
        if (QueryRaidPlayerDead is { } read)
            return read();
        var player = objectTable.LocalPlayer;
        return player is null ? null : player.IsDead || player.CurrentHp == 0;
    }

    private bool? ReadRaidLootOutstanding()
        => QueryRaidOutstandingLoot is { } read ? read() : RaidDutyPolicy.ReadOutstandingLoot();

    private bool CanDispatchRaidLeave(DutyContextSnapshot context, RaidDutyState raid)
        => raid.Matches(context) && raid.AutomaticExitAllowedNow
           && !context.IsUnsafeTransition && !context.IsCutscene && !context.InCombat
           && completedDuty == (context.TerritoryTypeId, context.ContentFinderConditionId)
           && ReadRaidPlayerDead() == false
           && ReadRaidLootOutstanding() == false;

    // Runs before any ordinary exit work, and is rechecked immediately before
    // native dispatch. A Raid leave never borrows the explicit leave's Return.
    private bool HoldOrCancelRaidLeave(DutyContextSnapshot context, bool nativeDispatch = false)
    {
        var raid = ReadRaidState(context);
        if (CurrentMode != OwnershipMode.Leaving)
            return true;
        if (raidLeaveSessionId is not null)
        {
            if (!raid.Enabled || raid.SessionId != raidLeaveSessionId)
            {
                Stop(context, "Raid automatic exit cancelled.");
                return true;
            }
            if (!CanDispatchRaidLeave(context, raid))
            {
                SetPhase(ExecutionPhase.LeavingDuty,
                    "Raid automatic exit is waiting for current permission, manual recovery and distributed loot.");
                return true;
            }
        }
        else if (raid.Enabled && CurrentMode == OwnershipMode.Leaving && !explicitLeaveRequested)
        {
            // ADS may finish its enabled chest work, but FR remains the sole
            // automatic exit owner. Switching Loot off cancels that work too.
            releaseAfterCompletionSweep = true;
            if (nativeDispatch || !raid.Matches(context) || !raid.AdsLootEnabled)
            {
                CompleteDuty(context.CurrentDuty?.EnglishName ?? "Raid duty");
                return true;
            }
        }
        return false;
    }
}
