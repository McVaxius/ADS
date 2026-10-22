using ADS.Models;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private bool crystalTowerPyreticActive;
    private CrystalTowerActionHold? crystalTowerActionHold;

    internal enum CrystalTowerActionSetting { ForbidActions, ForceDisabled }
    internal Func<CrystalTowerMovementSnapshot, CrystalTowerActionSetting, bool, IReadOnlyList<string>, bool>?
        WriteCrystalTowerActionOverride { get; set; }

    private bool TryHoldCrystalTowerPyretic(DutyContextSnapshot context)
    {
        StopMovementAssists();
        targetManager.Target = null;
        crystalTowerTarget = 0;
        crystalTowerPyreticActive = true;
        var paused = TryAcquireCrystalTowerActionHold(context, crystalTowerOwner?.EntityId ?? 0,
            GetCrystalTowerStrategy(context), out _);
        return HoldCrystalTower(paused ? "Crystal Tower: Pyretic; ADS progression is paused."
            : "Crystal Tower: waiting for the movement provider.");
    }

    private bool ReleaseCrystalTowerPyretic(DutyContextSnapshot? context, bool discard = false)
    {
        var restored = ReleaseCrystalTowerActionHold(context, discard);
        crystalTowerPyreticActive = !restored && !discard;
        return restored;
    }

    internal bool TryAcquireCrystalTowerActionHold(DutyContextSnapshot context, uint localEntityId,
        CrystalTowerStrategyMode strategy, out string reason)
    {
        reason = "The provider action pause is unconfirmed.";
        if (crystalTowerActionHold is { Invalidated: true })
            return false;
        var movementReady = TryAcquireCrystalTowerMovement(context, localEntityId, strategy, out reason);
        if (crystalTowerMovementLease is not { Invalidated: false } lease
            || !TryReadOwnedCrystalTowerMovement(lease, context, out var current))
        {
            if (crystalTowerActionHold is { } abandoned) abandoned.Invalidated = true;
            reason = "A known, owned provider is required for the action pause.";
            return false;
        }
        if (crystalTowerActionHold is { } retained && !ReferenceEquals(retained.Lease, lease))
        {
            retained.Invalidated = true;
            lease.Invalidated = true;
            return false;
        }
        var hold = crystalTowerActionHold ??= new CrystalTowerActionHold(lease, current);
        if (!CrystalTowerActionSelectionRestorable(hold.Original))
            return InvalidateCrystalTowerActionHold(hold);

        // Each setting is attempted once. Rejection can still have confirmed effects:
        // retain those in lease.Expected for guarded cleanup, without retrying the request.
        if (!hold.ActionsAttempted)
        {
            hold.ActionsAttempted = true;
            if (!current.ForbidActions)
            {
                if (!WriteCrystalTowerAction(hold, context, CrystalTowerActionSetting.ForbidActions, true,
                        out var accepted, out var reached))
                    return false;
                hold.AdmissionRejected |= !accepted || !reached;
            }
        }
        if (!hold.DisableAttempted)
        {
            hold.DisableAttempted = true;
            if (!lease.Expected.ForceDisabled)
            {
                if (!WriteCrystalTowerAction(hold, context, CrystalTowerActionSetting.ForceDisabled, true,
                        out var accepted, out var reached))
                    return false;
                hold.AdmissionRejected |= !accepted || !reached;
            }
        }
        if (!TryReadOwnedCrystalTowerMovement(lease, context, out current))
            return InvalidateCrystalTowerActionHold(hold);
        if (hold.AdmissionRejected || !movementReady || !IsCrystalTowerMovementQuiescent
            || !current.ForbidMovement || !current.ForbidActions || !CrystalTowerForcedSelection(current)
            || current.FrameTime <= hold.RequestFrameTime)
            return false;

        reason = "Provider actions and movement are paused in a fresh owned sample.";
        return true;
    }

    /// <summary>
    /// Restore actions/presets BEFORE movement cleanup. Failure stays blocked without retries;
    /// only terminal ownership reset/death may explicitly discard the retained failure.
    /// </summary>
    internal bool ReleaseCrystalTowerActionHold(DutyContextSnapshot? context, bool discard = false)
    {
        if (crystalTowerActionHold is not { } hold)
            return true;
        var restored = false;
        try
        {
            if (context is null || hold.Invalidated
                || !ReferenceEquals(crystalTowerMovementLease, hold.Lease)
                || !TryReadOwnedCrystalTowerMovement(hold.Lease, context, out var current))
                return InvalidateCrystalTowerActionHold(hold);

            // Keep the empty force-disable sentinel in place until the original action
            // flag has been restored. Neither cleanup step may overwrite manual changes.
            if (current.ForbidActions != hold.Original.ForbidActions
                && (!WriteCrystalTowerAction(hold, context, CrystalTowerActionSetting.ForbidActions,
                        hold.Original.ForbidActions, out _, out var actionsRestored) || !actionsRestored))
                return InvalidateCrystalTowerActionHold(hold);
            if (!TryReadOwnedCrystalTowerMovement(hold.Lease, context, out current))
                return InvalidateCrystalTowerActionHold(hold);
            if (!CrystalTowerActionTargetReached(current, CrystalTowerActionSetting.ForceDisabled,
                    hold.Original.ForceDisabled, hold.Original.ActivePresets)
                && (!WriteCrystalTowerAction(hold, context, CrystalTowerActionSetting.ForceDisabled,
                        hold.Original.ForceDisabled, out _, out var presetsRestored) || !presetsRestored))
                return InvalidateCrystalTowerActionHold(hold);
            restored = true;
            return true;
        }
        finally
        {
            if ((restored || discard) && ReferenceEquals(crystalTowerActionHold, hold)) crystalTowerActionHold = null;
        }
    }

    private bool WriteCrystalTowerAction(CrystalTowerActionHold hold, DutyContextSnapshot context,
        CrystalTowerActionSetting setting, bool value, out bool accepted, out bool reached)
    {
        accepted = reached = false;
        var lease = hold.Lease;
        if (hold.Invalidated || !ReferenceEquals(crystalTowerActionHold, hold)
            || !TryReadOwnedCrystalTowerMovement(lease, context, out var before))
            return InvalidateCrystalTowerActionHold(hold);
        try
        {
            accepted = WriteCrystalTowerActionOverride is { } write
                ? write(before, setting, value, hold.Original.ActivePresets.ToArray())
                : WriteNativeCrystalTowerAction(before.Kind, setting, value, hold.Original.ActivePresets);
        }
        catch { }
        var after = ReadCrystalTowerMovement().Snapshot;
        if (after is null || !ReferenceEquals(crystalTowerActionHold, hold)
            || !ReferenceEquals(crystalTowerMovementLease, lease) || lease.Invalidated
            || !TryReadCrystalTowerMovementOwner(context, lease.Owner.EntityId, lease.Owner.Strategy, out var owner)
            || owner != lease.Owner
            || !CrystalTowerActionDeltaCompatible(before, after, setting, value, hold.Original.ActivePresets))
            return InvalidateCrystalTowerActionHold(hold);

        // Authorize only the observed setting/preset delta. Existing movement checks
        // must see our updated expectation rather than mistake it for an operator edit.
        lease.Expected = after;
        hold.RequestFrameTime = after.FrameTime;
        reached = CrystalTowerActionTargetReached(after, setting, value, hold.Original.ActivePresets);
        return true;
    }

    private static bool CrystalTowerActionDeltaCompatible(CrystalTowerMovementSnapshot before,
        CrystalTowerMovementSnapshot after, CrystalTowerActionSetting setting, bool value, IReadOnlyList<string> originalNames)
    {
        var normalized = after with { ForbidActions = before.ForbidActions,
            ForceDisabled = before.ForceDisabled, ActivePresets = before.ActivePresets };
        if (!CrystalTowerMovementSameState(before, normalized) || before.ForbidMovement != after.ForbidMovement
            || after.FrameTime < before.FrameTime || after.LastModified < before.LastModified)
            return false;
        if (setting == CrystalTowerActionSetting.ForbidActions)
            return after.ForceDisabled == before.ForceDisabled && CrystalTowerPresetNamesMatch(after, before.ActivePresets);
        if (after.ForbidActions != before.ForbidActions || after.LastModified != before.LastModified)
            return false;
        if (after.ForceDisabled == before.ForceDisabled && CrystalTowerPresetNamesMatch(after, before.ActivePresets))
            return true; // Ignored or rejected with no effects.
        if (CrystalTowerActionTargetReached(after, setting, value, originalNames))
            return true;
        // awgil clears the list before adding the sentinel or activating saved presets;
        // retain a verified partial prefix for cleanup even when a setter throws.
        return after.Kind == CrystalTowerMovementProvider.BossMod && !after.ForceDisabled
            && (value ? after.ActivePresets.Count == 0
                : OrdinaryPresets(after).SequenceEqual(originalNames.Where(name => name != "VBM Multibox")
                    .Take(OrdinaryPresets(after).Count()), StringComparer.Ordinal));
    }

    private static bool CrystalTowerActionTargetReached(CrystalTowerMovementSnapshot state,
        CrystalTowerActionSetting setting, bool value, IReadOnlyList<string> originalNames)
        => setting == CrystalTowerActionSetting.ForbidActions ? state.ForbidActions == value
            : value ? CrystalTowerForcedSelection(state)
            : !state.ForceDisabled && CrystalTowerPresetNamesMatch(state, originalNames);

    private static bool CrystalTowerForcedSelection(CrystalTowerMovementSnapshot state)
        => state.ForceDisabled && state.ActivePresets.SequenceEqual(new[] { string.Empty }, StringComparer.Ordinal);

    private static bool CrystalTowerPresetNamesMatch(CrystalTowerMovementSnapshot state, IReadOnlyList<string> names)
        => OrdinaryPresets(state).SequenceEqual(state.Kind == CrystalTowerMovementProvider.BossMod
            ? names.Where(name => name != "VBM Multibox") : names, StringComparer.Ordinal);

    private static bool CrystalTowerActionSelectionRestorable(CrystalTowerMovementSnapshot state)
        => state.ForceDisabled ? CrystalTowerForcedSelection(state)
            : !state.ActivePresets.Any(string.IsNullOrEmpty)
                && (state.Kind != CrystalTowerMovementProvider.BossModReborn || state.ActivePresets.Count <= 1);

    private static bool InvalidateCrystalTowerActionHold(CrystalTowerActionHold hold)
    {
        hold.Invalidated = true;
        hold.Lease.Invalidated = true;
        return false;
    }

    private static bool WriteNativeCrystalTowerAction(CrystalTowerMovementProvider kind,
        CrystalTowerActionSetting setting, bool value, IReadOnlyList<string> originalNames)
    {
        var pi = Plugin.PluginInterface;
        if (pi is null) return false;
        if (setting == CrystalTowerActionSetting.ForbidActions)
        {
            // d409d336 IpcService/ConfigRoot: save is ignored and Modified fires.
            if (kind == CrystalTowerMovementProvider.BossMod)
            {
                var set = pi.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration");
                return set.HasFunction && set.InvokeFunc(new[] { "AIConfig", "ForbidActions", value.ToString() }, false) is { Count: 0 };
            }
            // ae64e330 ConfigRoot preserves an existing field override's original value.
            // Restore with this same per-field API; ClearTransient would affect other callers.
            var transient = pi.GetIpcSubscriber<List<string>, List<string>>("BossMod.Configuration.SetTransient");
            return transient.HasFunction && transient.InvokeFunc(new() { "AIConfig", "ForbidActions", value.ToString() }) is { Count: 0 };
        }
        // Both pinned SetForceDisabled implementations replace the active selection
        // with the private empty-name sentinel. There is no ClearForceDisabled gate.
        if (value)
        {
            var disable = pi.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled");
            return disable.HasFunction && disable.InvokeFunc();
        }
        if (kind == CrystalTowerMovementProvider.BossMod)
        {
            var restore = pi.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList");
            return restore.HasFunction && restore.InvokeFunc(originalNames.ToList());
        }
        if (originalNames.Count == 0)
        {
            var clear = pi.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
            return clear.HasFunction && clear.InvokeFunc();
        }
        var active = pi.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        return originalNames.Count == 1 && active.HasFunction && active.InvokeFunc(originalNames[0]);
    }

    private sealed class CrystalTowerActionHold(CrystalTowerMovementLease lease, CrystalTowerMovementSnapshot original)
    {
        internal CrystalTowerMovementLease Lease { get; } = lease;
        internal CrystalTowerMovementSnapshot Original { get; } = original with { ActivePresets = original.ActivePresets.ToArray() };
        internal DateTime RequestFrameTime { get; set; } = DateTime.MinValue;
        internal bool ActionsAttempted, DisableAttempted, AdmissionRejected, Invalidated;
    }
}
