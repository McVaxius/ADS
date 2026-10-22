using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin.Ipc;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerMovementLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeInteractionPauseReceivesRestoredMovementBeforeChangingAiOrPresets(bool originallyPaused)
    {
        var f = new Fixture();
        f.State = f.State with { ForbidMovement = originallyPaused };
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.Execution.InteractionVbmCharacterIdOverride = () => f.Cid;
        f.Execution.ReadInteractionVbmOverride = () => new(true, new(f.State.Provider, f.State.AiConfig,
            f.State.Runtime, f.State.ConfigRoot, f.State.AiEnabled!.Value, f.State.ForceDisabled, f.State.ActivePresets), "fixture");
        var writes = new List<string>();
        f.Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
        {
            Assert.Equal(originallyPaused, f.State.ForbidMovement);
            Assert.False(f.Execution.IsCrystalTowerMovementLeaseActive);
            writes.Add("ai");
            f.State = f.State with { AiEnabled = enabled, LastModified = f.State.LastModified.AddTicks(1) };
            return true;
        };
        f.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, names) =>
        {
            Assert.Equal(originallyPaused, f.State.ForbidMovement);
            writes.Add("presets");
            f.State = f.State with { ForceDisabled = disabled, ActivePresets = names.ToArray(),
                LastModified = f.State.LastModified.AddTicks(1) };
            return true;
        };

        Assert.True(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.False(f.State.AiEnabled);
        Assert.True(f.State.ForceDisabled);
        Assert.Equal(originallyPaused, f.State.ForbidMovement);
        f.Execution.ReleaseInteractionVbmPause(f.Context, allowRestore: true);
        Assert.True(f.State.AiEnabled);
        Assert.False(f.State.ForceDisabled);
        Assert.Equal(new[] { "Healing", "Damage" }, f.State.ActivePresets);
        Assert.Equal(originallyPaused, f.State.ForbidMovement);
        Assert.Equal(new[] { "ai", "presets", "ai", "presets" }, writes);

        // Preset/AI writes changed LastModified. A new movement owner now starts
        // from those restored values rather than retaining an invalid old lease.
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(originallyPaused, f.State.ForbidMovement);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("throws")]
    [InlineData("ignored")]
    public void FailedMovementRestorationCannotBeForgottenByLaterMovementOrInteractionTicks(string failure)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        f.Execution.WriteCrystalTowerMovementOverride = (_, forbid) =>
        {
            f.Writes.Add(forbid);
            if (failure == "throws") throw new InvalidOperationException("Synthetic cleanup rejection.");
            return failure == "ignored";
        };

        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.False(f.Execution.IsInteractionVbmPauseActive);
        Assert.True(f.State.ForbidMovement);
        Assert.Contains("restoration is unverified", f.Execution.InteractionVbmPauseStatus);

        // Even a now-working setter must not retry cleanup or capture our paused
        // flag as a new original on a later tick.
        f.Execution.WriteCrystalTowerMovementOverride = (_, forbid) => { f.ApplyWrite(forbid); return true; };
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.Equal(new[] { true, false }, f.Writes);
        Assert.Empty(f.InteractionWrites);
        Assert.True(f.State.ForbidMovement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableCleanupRetainsOriginalIntentEvenAfterReadsBecomeAvailable(bool originallyPaused)
    {
        var f = new Fixture();
        f.State = f.State with { ForbidMovement = originallyPaused };
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        f.Readable = false;
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        f.Readable = true;
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.True(f.State.ForbidMovement);
        Assert.Equal(originallyPaused ? Array.Empty<bool>() : new[] { true }, f.Writes);
        Assert.Empty(f.InteractionWrites);
    }

    [Theory]
    [InlineData("unloaded")]
    [InlineData("ambiguous")]
    public void MissingOrAmbiguousProviderCannotBypassRetainedCleanupFailure(string failure)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        if (failure == "unloaded") f.Loaded = false;
        else f.Readable = false;
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        f.Loaded = true;
        f.Readable = true;
        f.AdvanceFrame();
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.False(f.Acquire(out _));
        Assert.Equal(new[] { true }, f.Writes);
        Assert.Empty(f.InteractionWrites);
    }

    [Theory]
    [InlineData("lostReadback")]
    [InlineData("owner")]
    [InlineData("provider")]
    [InlineData("manual")]
    public void CleanupCannotProceedAfterAnUnverifiedSetterEffectOrOwnershipChange(string failure)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        f.AfterWrite = () =>
        {
            switch (failure)
            {
                case "lostReadback": f.Readable = false; break;
                case "owner": f.Cid++; break;
                case "provider": f.State = f.State with { Provider = new object() }; break;
                case "manual": f.State = f.State with { ActivePresets = new[] { "Manual" } }; break;
            }
        };
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        var changed = f.State;
        f.AfterWrite = null;
        f.Readable = true;
        f.AdvanceFrame();
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.False(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.False(f.State.ForbidMovement);
        Assert.Same(changed.Provider, f.State.Provider);
        Assert.Equal(changed.ActivePresets, f.State.ActivePresets);
        Assert.Equal(new[] { true, false }, f.Writes);
        Assert.Empty(f.InteractionWrites);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("config")]
    [InlineData("preset")]
    [InlineData("manualFlag")]
    [InlineData("cid")]
    [InlineData("alliance")]
    public void ProviderManualAndOwnerChangesBeforeCleanupAreRetainedAcrossTicks(string change)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        switch (change)
        {
            case "provider": f.State = f.State with { Provider = new object() }; break;
            case "config": f.State = f.State with { AiConfig = new object() }; break;
            case "preset": f.State = f.State with { ActivePresets = new[] { "Manual" } }; break;
            case "manualFlag": f.State = f.State with { ForbidMovement = false }; break;
            case "cid": f.Cid++; break;
            case "alliance": f.Context = Fixture.ContextFor(alliance: "B"); break;
        }
        var changed = f.State;
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        f.AdvanceFrame();
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.False(f.Acquire(out _));
        Assert.Equal(changed.ForbidMovement, f.State.ForbidMovement);
        Assert.Same(changed.Provider, f.State.Provider);
        Assert.Same(changed.AiConfig, f.State.AiConfig);
        Assert.Equal(changed.ActivePresets, f.State.ActivePresets);
        Assert.Equal(new[] { true }, f.Writes);
        Assert.Empty(f.InteractionWrites);
    }

    [Fact]
    public void OwnerChangeDuringPostCleanupReadbackCannotStartTheInteractionPause()
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        f.AfterRead = () =>
        {
            if (!f.Execution.IsCrystalTowerMovementLeaseActive) f.Cid++;
        };
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.Equal(new[] { true, false }, f.Writes);
        Assert.False(f.State.ForbidMovement);
        Assert.Empty(f.InteractionWrites);
    }

    [Theory]
    [InlineData("discard")]
    [InlineData("nullContext")]
    [InlineData("cancel")]
    public void ExplicitDiscardClearsRetainedFailureWithoutWritingAndAllowsANewOwner(string terminalCase)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.EnableInteractionProvider();
        f.Readable = false;
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        f.Readable = true;
        Assert.False(f.Execution.TryPauseInteractionVbm(f.Context));
        if (terminalCase == "cancel") f.Execution.CancelCrystalTower(f.Context);
        else f.Execution.ReleaseCrystalTowerMovement(terminalCase == "nullContext" ? null : f.Context,
            restore: terminalCase == "nullContext");
        Assert.Equal(new[] { true }, f.Writes);
        Assert.True(f.State.ForbidMovement);
        f.Cid++;
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        Assert.True(f.Execution.TryPauseInteractionVbm(f.Context));
        Assert.Equal(new[] { "ai", "presets" }, f.InteractionWrites);
        Assert.True(f.State.ForbidMovement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FreshQuietSampleAllowsMovementAndReleaseRestoresOnlyTheOriginalFlag(int provider)
    {
        var f = new Fixture((CrystalTowerMovementProvider)provider);
        Assert.False(f.Acquire(out var reason));
        Assert.Contains("fresh", reason);
        Assert.True(f.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.False(f.Execution.IsCrystalTowerMovementQuiescent);
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        Assert.True(f.Execution.IsCrystalTowerMovementQuiescent);
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);

        Assert.Equal(new[] { true, false }, f.Writes);
        Assert.False(f.State.ForbidMovement);
        Assert.Equal(new[] { "Healing", "Damage" }, f.State.ActivePresets);
        Assert.Equal(f.Original.AiEnabled, f.State.AiEnabled);
        Assert.Equal(f.Original.ForbidActions, f.State.ForbidActions);
        Assert.Equal(f.Original.ForceDisabled, f.State.ForceDisabled);
        Assert.False(f.Execution.IsCrystalTowerMovementLeaseActive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OriginallyPausedProviderAndCombatChoicesAreNeverEnabledByCleanup(bool enabled, bool forceDisabled)
    {
        var f = new Fixture();
        f.State = f.State with { ForbidMovement = true, AiEnabled = enabled, ForceDisabled = forceDisabled, ForbidActions = true };
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Empty(f.Writes);
        Assert.True(f.State.ForbidMovement);
        Assert.Equal(enabled, f.State.AiEnabled);
        Assert.Equal(forceDisabled, f.State.ForceDisabled);
        Assert.True(f.State.ForbidActions);
    }

    [Fact]
    public void MissingProviderNeedsNoPauseButUnavailableOrAmbiguousProviderBlocks()
    {
        var f = new Fixture { Loaded = false };
        Assert.True(f.Acquire(out _));
        Assert.False(f.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.True(f.Execution.IsCrystalTowerMovementQuiescent);
        f.Loaded = true;
        f.Readable = false;
        Assert.False(f.Acquire(out _));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public void MissingProviderAfterAcquisitionCannotTurnAnAbandonedLeaseIntoSuccess()
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.Loaded = false;
        Assert.False(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
    }

    [Theory]
    [InlineData("forced")]
    [InlineData("spin")]
    [InlineData("unreadable")]
    public void ActiveOrUnsupportedMovementCannotBeReportedAsQuiescent(string condition)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        f.State = condition switch
        {
            "forced" => f.State with { HasForcedMovement = true },
            "spin" => f.State with { HasSpinDirection = true },
            _ => f.State with { QuiescenceReadable = false },
        };
        Assert.False(f.Acquire(out _));
        Assert.True(f.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.False(f.Execution.IsCrystalTowerMovementQuiescent);
        Assert.Equal(new[] { true }, f.Writes);
        f.State = f.State with { HasForcedMovement = false, HasSpinDirection = false, QuiescenceReadable = true };
        Assert.True(f.Acquire(out _));
        f.State = f.State with { HasForcedMovement = true };
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.IsCrystalTowerMovementQuiescent);
    }

    [Theory]
    [InlineData("navigating")]
    [InlineData("target")]
    [InlineData("worker")]
    [InlineData("decision")]
    [InlineData("unknown")]
    public void BmrRequiresSettledControllerAndNoWorkerOrRetainedNavigationDecision(string condition)
    {
        var f = new Fixture(CrystalTowerMovementProvider.BossModReborn);
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        f.State = condition switch
        {
            "navigating" => f.State with { IsNavigating = true },
            "target" => f.State with { HasNavigationTarget = true },
            "worker" => f.State with { WorkerIdle = false },
            "decision" => f.State with { HasNavigationDecision = true },
            _ => f.State with { WorkerIdle = null },
        };
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.IsCrystalTowerMovementQuiescent);
        f.State = f.State with { IsNavigating = false, HasNavigationTarget = false, WorkerIdle = true, HasNavigationDecision = false };
        Assert.True(f.Acquire(out _));
        Assert.Equal(new[] { true }, f.Writes);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("configRoot")]
    [InlineData("config")]
    [InlineData("runtime")]
    [InlineData("hints")]
    [InlineData("aiManager")]
    [InlineData("behaviour")]
    [InlineData("preset")]
    [InlineData("forceDisable")]
    [InlineData("enabled")]
    [InlineData("actions")]
    [InlineData("manualFlag")]
    [InlineData("manualEvent")]
    public void ProviderOrManualStateChangesInvalidateWithoutRestoringOrReasserting(string change)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        var paused = f.State;
        f.State = change switch
        {
            "provider" => f.State with { Provider = new object() },
            "configRoot" => f.State with { ConfigRoot = new object() },
            "config" => f.State with { AiConfig = new object() },
            "runtime" => f.State with { Runtime = new object() },
            "hints" => f.State with { Hints = new object() },
            "aiManager" => f.State with { AiManager = new object() },
            "behaviour" => f.State with { Behaviour = new object() },
            "preset" => f.State with { ActivePresets = new[] { "Manual" } },
            "forceDisable" => f.State with { ForceDisabled = true },
            "enabled" => f.State with { AiEnabled = false },
            "actions" => f.State with { ForbidActions = true },
            "manualFlag" => f.State with { ForbidMovement = false },
            _ => f.State with { LastModified = f.State.LastModified.AddSeconds(1) },
        };
        Assert.False(f.Acquire(out _));
        Assert.False(f.Execution.IsCrystalTowerMovementLeaseActive);
        // Returning to the old values does not resurrect an invalidated lease.
        f.State = paused;
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
    }

    [Theory]
    [InlineData("cid")]
    [InlineData("entity")]
    [InlineData("alliance")]
    [InlineData("duty")]
    [InlineData("mode")]
    [InlineData("strategy")]
    [InlineData("transition")]
    [InlineData("logout")]
    [InlineData("disabled")]
    public void OwnerLossPreventsRestoreAndCannotBeReused(string change)
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        switch (change)
        {
            case "cid": f.Cid++; break;
            case "entity": f.EntityId++; break;
            case "alliance": f.Context = Fixture.ContextFor(alliance: "B"); break;
            case "duty": f.Context = Fixture.ContextFor(territory: 372, content: 102); break;
            case "mode": f.SetMode(OwnershipMode.Observing); break;
            case "strategy": f.Strategy = CrystalTowerStrategyMode.Unsync; break;
            case "transition": f.Context = Fixture.ContextFor(transition: true); break;
            case "logout": f.Context = Fixture.ContextFor(loggedIn: false); break;
            case "disabled": f.Context = Fixture.ContextFor(enabled: false); break;
        }
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
        Assert.True(f.State.ForbidMovement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedSetterWithConfirmedPartialEffectsWaitsForGuardedRelease(bool throws)
    {
        var f = new Fixture();
        f.Execution.WriteCrystalTowerMovementOverride = (_, forbid) =>
        {
            f.ApplyWrite(forbid);
            if (throws && forbid)
                throw new InvalidOperationException("Synthetic rejection after assignment.");
            return !forbid;
        };
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        Assert.Equal(new[] { true }, f.Writes);
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true, false }, f.Writes);
        Assert.False(f.State.ForbidMovement);
    }

    [Fact]
    public void IgnoredSetterCannotBeConfirmedOrRetriedByTheSameLease()
    {
        var f = new Fixture();
        f.Execution.WriteCrystalTowerMovementOverride = (_, forbid) => { f.Writes.Add(forbid); return true; };
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        Assert.False(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
        Assert.False(f.State.ForbidMovement);
    }

    [Theory]
    [InlineData("lostReadback")]
    [InlineData("owner")]
    [InlineData("provider")]
    [InlineData("manual")]
    [InlineData("reentrantRelease")]
    public void ChangesDuringSetterCannotContinueOrBlindlyRestore(string change)
    {
        var f = new Fixture();
        f.AfterWrite = () =>
        {
            switch (change)
            {
                case "lostReadback": f.Readable = false; break;
                case "owner": f.Cid++; break;
                case "provider": f.State = f.State with { Provider = new object() }; break;
                case "manual": f.State = f.State with { ForbidMovement = false }; break;
                case "reentrantRelease": f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: false); break;
            }
        };
        Assert.False(f.Acquire(out _));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
        Assert.False(f.Execution.IsCrystalTowerMovementQuiescent);
    }

    [Fact]
    public void OwnerChangeDuringReadPreventsAnIpcWrite()
    {
        var f = new Fixture();
        f.AfterRead = () => f.Cid++;
        Assert.False(f.Acquire(out _));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public void ReleaseDuringRetainedLeaseReadCannotReacquireInTheSameCall()
    {
        var f = new Fixture();
        Assert.False(f.Acquire(out _));
        f.AdvanceFrame();
        f.AfterRead = () => f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: false);
        Assert.False(f.Acquire(out _));
        Assert.Equal(new[] { true }, f.Writes);
        Assert.False(f.Execution.IsCrystalTowerMovementLeaseActive);
    }

    [Fact]
    public void MutablePresetListsAreCopiedAndNativeMultiboxReorderingIsTolerated()
    {
        var f = new Fixture();
        var names = new List<string> { "VBM Multibox", "Healing", "Damage" };
        f.State = f.State with { ActivePresets = names };
        Assert.False(f.Acquire(out _));
        names.RemoveAt(0);
        names.Add("VBM Multibox");
        f.AdvanceFrame();
        Assert.True(f.Acquire(out _));
        names[0] = "Manual";
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(new[] { true }, f.Writes);
        Assert.Equal(new[] { "Manual", "Damage", "VBM Multibox" }, names);
    }

    [Fact]
    public void ExplicitDiscardAndNullContextDoNotEnableProviderMovement()
    {
        foreach (var nullContext in new[] { false, true })
        {
            var f = new Fixture();
            Assert.False(f.Acquire(out _));
            f.Execution.ReleaseCrystalTowerMovement(nullContext ? null : f.Context, restore: nullContext);
            Assert.Equal(new[] { true }, f.Writes);
        }
    }

    [Fact]
    public void InvalidStrategiesAndUnknownProviderContractsDoNotWrite()
    {
        foreach (var strategy in new[] { CrystalTowerStrategyMode.None, CrystalTowerStrategyMode.Invalid })
        {
            var f = new Fixture { Strategy = strategy };
            Assert.False(f.Acquire(out _));
            Assert.Empty(f.Writes);
        }
        var unknown = new Fixture();
        unknown.State = unknown.State with { Kind = (CrystalTowerMovementProvider)99 };
        Assert.False(unknown.Acquire(out _));
        Assert.Empty(unknown.Writes);
    }

    [Fact]
    public void BooleanReadbackRejectsErrorsAndAmbiguousResponses()
    {
        Assert.True(ParseCrystalTowerMovementConfig(new[] { "True" }));
        Assert.False(ParseCrystalTowerMovementConfig(new[] { "False" }));
        foreach (var result in new IReadOnlyList<string>?[] { null, Array.Empty<string>(), new[] { "True", "False" }, new[] { "Unknown field" } })
            Assert.Throws<InvalidOperationException>(() => ParseCrystalTowerMovementConfig(result));
    }

    [Fact]
    public void PinnedBmrAssignmentRegistrationProducesABooleanFunctionRatherThanAnAction()
    {
        // Exact overload/assignment shape from ae64e330 IPCProvider. C# selects the Func overload.
        var producer = new PauseRegistration();
        producer.RegisterPause();
        Assert.Equal(typeof(Func<bool, bool>), producer.Callback!.GetType());
        var call = (Func<bool, bool>)producer.Callback;
        Assert.True(call(true));
        Assert.True(producer.Forbid);
        Assert.False(call(false));
        Assert.False(producer.Forbid);
    }

    [Fact]
    public void NativePauseGateTreatsTheReturnedAssignedFalseAsSuccessfulRestoration()
    {
        var gate = DispatchProxy.Create<ICallGateSubscriber<bool, bool>, PauseGateProxy>();
        var proxy = (PauseGateProxy)(object)gate;
        Assert.True(InvokeCrystalTowerMovementPause(gate, true));
        Assert.True(proxy.Forbid);
        Assert.True(InvokeCrystalTowerMovementPause(gate, false));
        Assert.False(proxy.Forbid);
        Assert.Equal(new[] { true, false }, proxy.Writes);
        proxy.Available = false;
        Assert.False(InvokeCrystalTowerMovementPause(gate, true));
        Assert.Equal(new[] { true, false }, proxy.Writes);
    }

    public class PauseGateProxy : DispatchProxy
    {
        internal bool Available = true, Forbid;
        internal List<bool> Writes = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_HasFunction") return Available;
            if (method.Name == "InvokeFunc")
            {
                Writes.Add(Forbid = (bool)args![0]!);
                return Forbid;
            }
            throw new InvalidOperationException("The pinned producer exports a function, not an action.");
        }
    }

    private sealed class PauseRegistration
    {
        internal bool Forbid;
        internal Delegate? Callback;
        internal void RegisterPause() => Register("AI.PauseMovement", (bool pause) => Forbid = pause);
        private void Register<T, TResult>(string name, Func<T, TResult> callback) => Callback = callback;
        private void Register<T>(string name, Action<T> callback) => Callback = callback;
    }

    private sealed class Fixture
    {
        internal Fixture(CrystalTowerMovementProvider provider = CrystalTowerMovementProvider.BossMod)
        {
            var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            State = new(provider, new(), new(), new(), new(), new(), provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                false, provider == CrystalTowerMovementProvider.BossMod ? true : null, false, false, new[] { "Healing", "Damage" },
                time, time, true, false, false, false, false, true, false);
            Original = State;
            Execution = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Configuration());
            SetMode(OwnershipMode.OwnedStartInside);
            Execution.CrystalTowerMovementCharacterIdOverride = () => Cid;
            Execution.CrystalTowerMovementLocalEntityIdOverride = () => EntityId;
            Execution.CrystalTowerMovementStrategyOverride = _ => Strategy;
            Execution.ReadCrystalTowerMovementOverride = () =>
            {
                var result = !Loaded ? new CrystalTowerMovementReadResult(false, null, string.Empty)
                    : !Readable ? new(true, null, "Synthetic unreadable/ambiguous provider.")
                    : new(true, State, string.Empty);
                AfterRead?.Invoke();
                return result;
            };
            Execution.WriteCrystalTowerMovementOverride = (_, forbid) => { ApplyWrite(forbid); return true; };
        }

        internal ExecutionService Execution { get; }
        internal CrystalTowerMovementSnapshot Original { get; }
        internal CrystalTowerMovementSnapshot State;
        internal DutyContextSnapshot Context = ContextFor();
        internal ulong Cid = 1;
        internal uint EntityId = 123;
        internal CrystalTowerStrategyMode Strategy = CrystalTowerStrategyMode.Sync;
        internal bool Loaded = true, Readable = true;
        internal Action? AfterWrite, AfterRead;
        internal List<bool> Writes { get; } = [];
        internal List<string> InteractionWrites { get; } = [];
        internal void EnableInteractionProvider()
        {
            Execution.InteractionVbmCharacterIdOverride = () => Cid;
            Execution.ReadInteractionVbmOverride = () => !Loaded ? new(false, null, string.Empty)
                : new(true, new(State.Provider, State.AiConfig, State.Runtime, State.ConfigRoot,
                    State.AiEnabled!.Value, State.ForceDisabled, State.ActivePresets), "fixture");
            Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
            {
                InteractionWrites.Add("ai");
                State = State with { AiEnabled = enabled, LastModified = State.LastModified.AddTicks(1) };
                return true;
            };
            Execution.WriteInteractionVbmPresetsOverride = (_, disabled, names) =>
            {
                InteractionWrites.Add("presets");
                State = State with { ForceDisabled = disabled, ActivePresets = names.ToArray(),
                    LastModified = State.LastModified.AddTicks(1) };
                return true;
            };
        }
        internal bool Acquire(out string reason)
            => Execution.TryAcquireCrystalTowerMovement(Context, 123, CrystalTowerStrategyMode.Sync, out reason);
        internal void AdvanceFrame() => State = State with { FrameTime = State.FrameTime.AddMilliseconds(16) };
        internal void ApplyWrite(bool forbid)
        {
            Writes.Add(forbid);
            State = State with { ForbidMovement = forbid,
                LastModified = State.Kind == CrystalTowerMovementProvider.BossMod ? State.LastModified.AddTicks(1) : State.LastModified };
            AfterWrite?.Invoke();
        }
        internal void SetMode(OwnershipMode mode)
            => typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(Execution, mode);
        internal static DutyContextSnapshot ContextFor(string alliance = "A", uint territory = 174, uint content = 92,
            bool transition = false, bool loggedIn = true, bool enabled = true)
            => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: territory, cfcId: content,
                alliance: alliance, betweenAreas: transition, loggedIn: loggedIn, pluginEnabled: enabled);
    }
}
