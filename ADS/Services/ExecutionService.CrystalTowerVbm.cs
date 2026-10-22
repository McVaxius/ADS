using System.Numerics;
using System.Reflection;
using ADS.Models;
using Dalamud.Plugin.Ipc;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private CrystalTowerMovementLease? crystalTowerMovementLease;

    internal Func<CrystalTowerMovementReadResult>? ReadCrystalTowerMovementOverride { get; set; }
    internal Func<CrystalTowerMovementSnapshot, bool, bool>? WriteCrystalTowerMovementOverride { get; set; }
    internal Func<ulong>? CrystalTowerMovementCharacterIdOverride { get; set; }
    internal Func<uint>? CrystalTowerMovementLocalEntityIdOverride { get; set; }
    internal Func<DutyContextSnapshot, CrystalTowerStrategyMode>? CrystalTowerMovementStrategyOverride { get; set; }

    internal bool IsCrystalTowerMovementLeaseActive
        => crystalTowerMovementLease is { Invalidated: false, Expected.ForbidMovement: true };
    internal bool IsCrystalTowerMovementQuiescent { get; private set; }
    internal string CrystalTowerMovementStatus { get; private set; } = "No Crystal Tower movement lease.";

    // Success describes this sample only. Call again before every owned move/hold tick:
    // neither provider's flag globally suppresses movement from arbitrary rotation modules.
    internal bool TryAcquireCrystalTowerMovement(DutyContextSnapshot context, uint localEntityId,
        CrystalTowerStrategyMode mode, out string reason)
    {
        IsCrystalTowerMovementQuiescent = false;
        if (!TryReadCrystalTowerMovementOwner(context, localEntityId, mode, out var owner))
            return CrystalTowerMovementBlocked("The Crystal Tower movement owner is unavailable.", out reason, invalidate: true);

        var beforeReadLease = crystalTowerMovementLease;
        var read = ReadCrystalTowerMovement();
        if (!ReferenceEquals(beforeReadLease, crystalTowerMovementLease))
            return CrystalTowerMovementBlocked("The Crystal Tower movement lease changed during provider readback.", out reason);
        if (!TryReadCrystalTowerMovementOwner(context, localEntityId, mode, out var afterReadOwner) || owner != afterReadOwner)
            return CrystalTowerMovementBlocked("Crystal Tower ownership changed during provider readback.", out reason, invalidate: true);
        if (crystalTowerMovementLease is { } retained)
        {
            if (retained.Invalidated || retained.Owner != owner || read.Snapshot is not { } current
                || !CrystalTowerMovementMatches(current, retained.Expected))
                return CrystalTowerMovementBlocked("Provider, configuration or Crystal Tower ownership changed; movement held.", out reason, invalidate: true);
            retained.Expected = current;
        }
        else
        {
            if (!read.Loaded)
            {
                IsCrystalTowerMovementQuiescent = true;
                reason = CrystalTowerMovementStatus = "No BossMod provider is loaded.";
                return true;
            }
            if (read.Snapshot is not { } current)
                return CrystalTowerMovementBlocked(read.Reason, out reason);
            var lease = new CrystalTowerMovementLease(owner, current);
            crystalTowerMovementLease = lease;
            if (!current.ForbidMovement)
                lease.RequestAccepted = WriteCrystalTowerMovement(lease, context, true);
        }

        if (crystalTowerMovementLease is not { } active || active.Invalidated || !active.RequestAccepted
            || !TryReadOwnedCrystalTowerMovement(active, context, out var paused)
            || !paused.ForbidMovement)
            return CrystalTowerMovementBlocked("The provider movement pause could not be confirmed.", out reason);
        if (paused.FrameTime <= active.RequestFrameTime)
            return CrystalTowerMovementBlocked("Movement pause requested; awaiting a fresh provider update.", out reason);
        if (!paused.QuiescenceReadable)
            return CrystalTowerMovementBlocked("Provider movement quiescence is unsupported or unreadable.", out reason);
        if (paused.HasForcedMovement || paused.HasSpinDirection)
            return CrystalTowerMovementBlocked("The provider still requests movement; Crystal Tower movement held.", out reason);
        if (paused.Kind == CrystalTowerMovementProvider.BossModReborn
            && (paused.IsNavigating != false || paused.HasNavigationTarget != false
                || paused.WorkerIdle != true || paused.HasNavigationDecision != false))
            return CrystalTowerMovementBlocked("Movement pause requested; awaiting BossModReborn navigation settling.", out reason);

        IsCrystalTowerMovementQuiescent = true;
        reason = CrystalTowerMovementStatus = "Provider movement pause confirmed in the current sample; combat presets retained.";
        return true;
    }

    internal void ReleaseCrystalTowerMovement(DutyContextSnapshot? context, bool restore)
    {
        IsCrystalTowerMovementQuiescent = false;
        if (crystalTowerMovementLease is not { } lease)
            return;
        var restored = false;
        try
        {
            if (!restore || context is null)
            {
                CrystalTowerMovementStatus = "Movement lease discarded; current provider state retained.";
                return;
            }
            if (lease.Invalidated || !TryReadOwnedCrystalTowerMovement(lease, context, out var current))
            {
                CrystalTowerMovementStatus = "Provider movement restoration is unverified; movement lease retained.";
                return;
            }
            if (current.ForbidMovement == lease.Original.ForbidMovement)
            {
                restored = true;
                CrystalTowerMovementStatus = "The provider's original movement setting is retained.";
                return;
            }
            restored = WriteCrystalTowerMovement(lease, context, lease.Original.ForbidMovement);
            CrystalTowerMovementStatus = restored
                ? "The provider's original movement setting was restored."
                : "Provider movement restoration could not be confirmed.";
        }
        finally
        {
            if (ReferenceEquals(crystalTowerMovementLease, lease))
            {
                if (restored || !restore || context is null)
                    crystalTowerMovementLease = null;
                else
                    // Retain the original intent and block both reacquisition and
                    // another cleanup write until an explicit terminal discard.
                    lease.Invalidated = true;
            }
        }
    }

    private bool CrystalTowerMovementBlocked(string reasonText, out string reason, bool invalidate = false)
    {
        if (invalidate && crystalTowerMovementLease is { } lease)
            lease.Invalidated = true;
        reason = CrystalTowerMovementStatus = reasonText;
        return false;
    }

    private bool TryReadCrystalTowerMovementOwner(DutyContextSnapshot context, uint localEntityId,
        CrystalTowerStrategyMode strategy, out CrystalTowerMovementOwner owner)
    {
        owner = default;
        if (!IsActiveOwnedDutyMode() || !context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty
            || context.IsUnsafeTransition || context.IsCutscene || context.Alliance is not ("A" or "B" or "C")
            || !CrystalTowerStrategy.MatchesDuty(context.TerritoryTypeId, context.ContentFinderConditionId)
            || strategy is not (CrystalTowerStrategyMode.Sync or CrystalTowerStrategyMode.Unsync)
            || localEntityId is 0 or 0xE0000000 or uint.MaxValue)
            return false;
        try
        {
            var cid = CrystalTowerMovementCharacterIdOverride?.Invoke() ?? Plugin.PlayerState?.ContentId ?? 0;
            var entityId = CrystalTowerMovementLocalEntityIdOverride?.Invoke() ?? objectTable.LocalPlayer?.EntityId ?? 0;
            var currentStrategy = CrystalTowerMovementStrategyOverride?.Invoke(context)
                ?? CrystalTowerStrategy.Resolve(objectPriorityRuleService.Current.Rules, context);
            if (cid == 0 || entityId != localEntityId || currentStrategy != strategy)
                return false;
            owner = new(cid, context.TerritoryTypeId, context.ContentFinderConditionId, context.Alliance,
                entityId, CurrentMode, strategy);
            return true;
        }
        catch { return false; }
    }

    private bool TryReadOwnedCrystalTowerMovement(CrystalTowerMovementLease lease, DutyContextSnapshot context,
        out CrystalTowerMovementSnapshot current)
    {
        current = null!;
        if (lease.Invalidated || !ReferenceEquals(crystalTowerMovementLease, lease)
            || !TryReadCrystalTowerMovementOwner(context, lease.Owner.EntityId, lease.Owner.Strategy, out var owner)
            || owner != lease.Owner)
        {
            lease.Invalidated = true;
            return false;
        }
        var read = ReadCrystalTowerMovement();
        if (read.Snapshot is not { } snapshot || !CrystalTowerMovementMatches(snapshot, lease.Expected)
            || !ReferenceEquals(crystalTowerMovementLease, lease)
            || !TryReadCrystalTowerMovementOwner(context, lease.Owner.EntityId, lease.Owner.Strategy, out var afterReadOwner)
            || afterReadOwner != lease.Owner)
        {
            lease.Invalidated = true;
            return false;
        }
        lease.Expected = current = snapshot;
        return true;
    }

    private bool WriteCrystalTowerMovement(CrystalTowerMovementLease lease, DutyContextSnapshot context, bool forbid)
    {
        if (!TryReadOwnedCrystalTowerMovement(lease, context, out var before))
            return false;
        var accepted = false;
        try
        {
            accepted = WriteCrystalTowerMovementOverride is { } write
                ? write(before, forbid) : WriteNativeCrystalTowerMovement(before.Kind, forbid);
        }
        catch { }
        var after = ReadCrystalTowerMovement().Snapshot;
        if (after is null || !ReferenceEquals(crystalTowerMovementLease, lease)
            || !TryReadCrystalTowerMovementOwner(context, lease.Owner.EntityId, lease.Owner.Strategy, out var owner)
            || owner != lease.Owner || !CrystalTowerMovementSameState(before, after)
            || after.FrameTime < before.FrameTime
            || before.Kind == CrystalTowerMovementProvider.BossModReborn && before.LastModified != after.LastModified)
        {
            lease.Invalidated = true;
            return false;
        }
        // Keep confirmed partial effects even when a setter rejects/throws. Only a later
        // guarded release may restore them; acquisition never blindly repeats a write.
        lease.Expected = after;
        lease.RequestFrameTime = after.FrameTime;
        return accepted && after.ForbidMovement == forbid;
    }

    private CrystalTowerMovementReadResult ReadCrystalTowerMovement()
    {
        try
        {
            var read = ReadCrystalTowerMovementOverride?.Invoke() ?? ReadNativeCrystalTowerMovement();
            if (read.Snapshot is not { } state)
                return read;
            if (!read.Loaded || state.Kind is not (CrystalTowerMovementProvider.BossMod or CrystalTowerMovementProvider.BossModReborn)
                || state.Provider is null || state.ConfigRoot is null || state.AiConfig is null || state.Runtime is null
                || state.Hints is null || state.ActivePresets is null || state.ActivePresets.Any(name => name is null)
                || state.FrameTime == default)
                return new(true, null, "Provider movement state is unreadable.");
            return read with { Snapshot = state with { ActivePresets = state.ActivePresets.ToArray() } };
        }
        catch { return new(true, null, "Provider movement state is unavailable."); }
    }

    private static bool CrystalTowerMovementSameState(CrystalTowerMovementSnapshot a, CrystalTowerMovementSnapshot b)
        => a.Kind == b.Kind && ReferenceEquals(a.Provider, b.Provider) && ReferenceEquals(a.ConfigRoot, b.ConfigRoot)
            && ReferenceEquals(a.AiConfig, b.AiConfig) && ReferenceEquals(a.Runtime, b.Runtime)
            && ReferenceEquals(a.Hints, b.Hints) && ReferenceEquals(a.AiManager, b.AiManager)
            && ReferenceEquals(a.Behaviour, b.Behaviour) && a.AiEnabled == b.AiEnabled
            && a.ForbidActions == b.ForbidActions && a.ForceDisabled == b.ForceDisabled
            && OrdinaryPresets(a).SequenceEqual(OrdinaryPresets(b), StringComparer.Ordinal);

    private static IEnumerable<string> OrdinaryPresets(CrystalTowerMovementSnapshot state)
        => state.Kind == CrystalTowerMovementProvider.BossMod
            ? state.ActivePresets.Where(name => name != "VBM Multibox") : state.ActivePresets;

    private static bool CrystalTowerMovementMatches(CrystalTowerMovementSnapshot a, CrystalTowerMovementSnapshot b)
        => CrystalTowerMovementSameState(a, b) && a.ForbidMovement == b.ForbidMovement
            && a.LastModified == b.LastModified && a.FrameTime >= b.FrameTime;

    private static CrystalTowerMovementReadResult ReadNativeCrystalTowerMovement()
    {
        var pi = Plugin.PluginInterface;
        if (pi is null)
            return new(true, null, "No plugin host is available.");
        var loaded = pi.InstalledPlugins.Where(plugin => plugin.IsLoaded
            && plugin.InternalName is "BossMod" or "BossModReborn").ToArray();
        if (loaded.Length == 0)
            return new(false, null, string.Empty);
        if (loaded.Length != 1)
            return new(true, null, "BossMod IPC provider is ambiguous.");

        var kind = loaded[0].InternalName == "BossMod" ? CrystalTowerMovementProvider.BossMod : CrystalTowerMovementProvider.BossModReborn;
        var local = FindInteractionVbmLocalPlugin(loaded[0], loaded[0].GetType().Assembly, 4, new(ReferenceEqualityComparer.Instance));
        var provider = local is null ? null : ReadInteractionVbmMember(local, "instance");
        var assembly = local is null ? null : ReadInteractionVbmMember(local, "Assembly") as Assembly;
        if (provider is null || assembly is null || provider.GetType().Assembly != assembly)
            return new(true, null, "The live provider instance is unreadable.");
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var serviceType = assembly.GetType("BossMod.Service");
        var config = serviceType?.GetProperty("Config", statics)?.GetValue(null) ?? serviceType?.GetField("Config", statics)?.GetValue(null);
        var aiType = assembly.GetType("BossMod.AI.AIConfig");
        var ai = config is null || aiType is null ? null : ReadInteractionVbmAiConfig(config, aiType, assembly);
        object? runtime, aiManager = null, behaviour = null;
        if (kind == CrystalTowerMovementProvider.BossMod)
        {
            // d409d336 Plugin registers this exact live service. No TickService
            // private/generated constructor fields are part of this contract.
            var host = ReadInteractionVbmMember(provider, "Host");
            var services = host is null ? null : ReadInteractionVbmMember(host, "Services") as IServiceProvider;
            var runtimeType = assembly.GetType("BossMod.Autorotation.RotationModuleManager");
            runtime = runtimeType is null ? null : services?.GetService(runtimeType);
        }
        else
        {
            // ae64e330 Plugin declares both fields; behaviour replacement invalidates ownership.
            runtime = ReadInteractionVbmMember(provider, "_rotation");
            aiManager = ReadInteractionVbmMember(provider, "_ai");
            if (aiManager is null || !TryReadCrystalTowerMovementMember(aiManager, "Beh", out behaviour))
                return new(true, null, "BossModReborn AI identity is unreadable.");
        }
        var hints = runtime is null ? null : ReadInteractionVbmMember(runtime, "Hints");
        var world = runtime is null ? null : ReadInteractionVbmMember(runtime, "WorldState");
        if (ai is null || runtime is null || hints is null || world is null
            || runtime.GetType() != assembly.GetType("BossMod.Autorotation.RotationModuleManager")
            || hints.GetType() != assembly.GetType("BossMod.AIHints")
            || ReadInteractionVbmMember(world, "CurrentTime") is not DateTime frameTime)
            return new(true, null, "The provider configuration or live movement runtime is unreadable.");

        var forbid = ReadCrystalTowerMovementConfig(kind, "ForbidMovement");
        var forbidActions = ReadCrystalTowerMovementConfig(kind, "ForbidActions");
        bool? enabled = kind == CrystalTowerMovementProvider.BossMod ? ReadCrystalTowerMovementConfig(kind, "Enabled") : null;
        if (ReadInteractionVbmMember(ai, "ForbidMovement") is not bool boundForbid || boundForbid != forbid
            || ReadInteractionVbmMember(ai, "ForbidActions") is not bool boundActions || boundActions != forbidActions
            || kind == CrystalTowerMovementProvider.BossMod && !Equals(ReadInteractionVbmMember(ai, "Enabled"), enabled))
            return new(true, null, "Provider configuration readback no longer matches its bound live instance.");
        var disabled = pi.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled");
        var modified = pi.GetIpcSubscriber<DateTime>("BossMod.Configuration.LastModified");
        if (!disabled.HasFunction || !modified.HasFunction)
            return new(true, null, "Provider configuration witnesses are unavailable.");
        IReadOnlyList<string>? presets;
        if (kind == CrystalTowerMovementProvider.BossMod)
        {
            var active = pi.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");
            if (!active.HasFunction)
                return new(true, null, "Provider presets are unreadable.");
            presets = active.InvokeFunc();
        }
        else
        {
            var active = pi.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
            if (!active.HasFunction)
                return new(true, null, "Provider preset is unreadable.");
            var name = active.InvokeFunc();
            presets = name is null ? [] : [name];
        }
        if (presets is null)
            return new(true, null, "Provider presets are unreadable.");

        var readable = TryReadCrystalTowerMovementMember(hints, "ForcedMovement", out var forced)
            && (forced is null or Vector3) && TryReadCrystalTowerMovementMember(hints, "SpinDirection", out _);
        TryReadCrystalTowerMovementMember(hints, "SpinDirection", out var spin);
        bool? navigating = null, hasTarget = null, workerIdle = null, hasDecision = null;
        if (kind == CrystalTowerMovementProvider.BossModReborn)
        {
            var isNavigating = pi.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating");
            var naviTarget = pi.GetIpcSubscriber<Vector3?>("BossMod.AI.NaviTargetPos");
            var behaviourType = assembly.GetType("BossMod.AI.AIBehaviour");
            var semaphore = behaviourType?.GetField("_semaphore", statics)?.GetValue(null) as System.Threading.SemaphoreSlim;
            var idleBefore = semaphore?.CurrentCount == 1;
            var decisionReadable = behaviour is null || TryReadCrystalTowerMovementMember(behaviour, "_naviDecision", out _);
            object? decision = behaviour is null ? null : ReadInteractionVbmMember(behaviour, "_naviDecision");
            var destinationReadable = behaviour is null || decision is not null
                && TryReadCrystalTowerMovementMember(decision, "Destination", out _);
            hasDecision = behaviour is null ? false : decision is null || ReadInteractionVbmMember(decision, "Destination") is not null;
            if (isNavigating.HasFunction && naviTarget.HasFunction && semaphore is not null)
            {
                navigating = isNavigating.InvokeFunc();
                hasTarget = naviTarget.InvokeFunc().HasValue;
                workerIdle = idleBefore && semaphore.CurrentCount == 1;
            }
            readable &= navigating.HasValue && hasTarget.HasValue && workerIdle.HasValue && decisionReadable && destinationReadable;
        }
        return new(true, new(kind, provider, config!, ai, runtime, hints, aiManager, behaviour,
            forbid, enabled, forbidActions, disabled.InvokeFunc(), presets, modified.InvokeFunc(), frameTime,
            readable, forced is not null, spin is not null, navigating, hasTarget, workerIdle, hasDecision), string.Empty);
    }

    internal static bool ParseCrystalTowerMovementConfig(IReadOnlyList<string>? result)
        => result is { Count: 1 } && bool.TryParse(result[0], out var value) ? value
            : throw new InvalidOperationException("Provider boolean configuration readback is invalid.");

    private static bool ReadCrystalTowerMovementConfig(CrystalTowerMovementProvider kind, string field)
    {
        var pi = Plugin.PluginInterface;
        var args = new List<string> { "AIConfig", field };
        return ParseCrystalTowerMovementConfig(kind == CrystalTowerMovementProvider.BossMod
            ? pi.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration").InvokeFunc(args, false)
            : pi.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration").InvokeFunc(args, false));
    }

    private static bool WriteNativeCrystalTowerMovement(CrystalTowerMovementProvider kind, bool forbid)
    {
        var pi = Plugin.PluginInterface;
        if (kind == CrystalTowerMovementProvider.BossModReborn)
        {
            // ae64e330 IPCProvider's assignment lambda selects Register<bool,bool>(Func),
            // returning the assigned value. False is successful restoration, not rejection.
            return InvokeCrystalTowerMovementPause(pi.GetIpcSubscriber<bool, bool>("BossMod.AI.PauseMovement"), forbid);
        }
        // The pinned awgil source ignores 'save'; normal provider saving may persist
        // this flag. Guarded release restores the original value during normal cleanup.
        return pi.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration")
            .InvokeFunc(new[] { "AIConfig", "ForbidMovement", forbid.ToString() }, false) is { Count: 0 };
    }

    internal static bool InvokeCrystalTowerMovementPause(ICallGateSubscriber<bool, bool> pause, bool forbid)
        => pause.HasFunction && pause.InvokeFunc(forbid) == forbid;

    private static bool TryReadCrystalTowerMovementMember(object root, string name, out object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = root.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, flags) is { } field)
            {
                value = field.GetValue(root);
                return true;
            }
            if (type.GetProperty(name, flags) is { } property && property.GetIndexParameters().Length == 0)
            {
                value = property.GetValue(root);
                return true;
            }
        }
        value = null;
        return false;
    }

    internal enum CrystalTowerMovementProvider { BossMod, BossModReborn }
    internal sealed record CrystalTowerMovementReadResult(bool Loaded, CrystalTowerMovementSnapshot? Snapshot, string Reason);
    internal sealed record CrystalTowerMovementSnapshot(CrystalTowerMovementProvider Kind, object Provider, object ConfigRoot,
        object AiConfig, object Runtime, object Hints, object? AiManager, object? Behaviour, bool ForbidMovement,
        bool? AiEnabled, bool ForbidActions, bool ForceDisabled, IReadOnlyList<string> ActivePresets, DateTime LastModified,
        DateTime FrameTime, bool QuiescenceReadable, bool HasForcedMovement, bool HasSpinDirection,
        bool? IsNavigating, bool? HasNavigationTarget, bool? WorkerIdle, bool? HasNavigationDecision);
    private readonly record struct CrystalTowerMovementOwner(ulong Cid, uint Territory, uint Content, string Alliance,
        uint EntityId, OwnershipMode Mode, CrystalTowerStrategyMode Strategy);
    private sealed class CrystalTowerMovementLease(CrystalTowerMovementOwner owner, CrystalTowerMovementSnapshot original)
    {
        internal CrystalTowerMovementOwner Owner { get; } = owner;
        internal CrystalTowerMovementSnapshot Original { get; } = original;
        internal CrystalTowerMovementSnapshot Expected { get; set; } = original;
        internal DateTime RequestFrameTime { get; set; } = original.FrameTime;
        internal bool RequestAccepted { get; set; } = original.ForbidMovement;
        internal bool Invalidated { get; set; }
    }
}
