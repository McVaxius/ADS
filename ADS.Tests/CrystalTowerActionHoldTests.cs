using System.Reflection;
using ADS.Models;
using ADS.Services;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerActionHoldTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothProvidersPauseThroughTheSameLeaseAndRestoreSavedPresetsBeforeMovement(int provider)
    {
        var f = new Fixture((CrystalTowerMovementProvider)provider);
        Assert.False(f.Acquire());
        Assert.True(f.State.ForbidMovement);
        Assert.True(f.State.ForbidActions);
        Assert.True(f.State.ForceDisabled);
        Assert.Equal(new[] { string.Empty }, f.State.ActivePresets);
        Assert.Equal(f.Original.AiEnabled, f.State.AiEnabled);
        Assert.Equal(new[] { "Movement:True", "ForbidActions:True", "ForceDisabled:True" }, f.Writes);
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        Assert.True(f.Execution.IsCrystalTowerMovementQuiescent);
        var writes = f.Writes.Count;
        Assert.True(f.Acquire());
        Assert.Equal(writes, f.Writes.Count);

        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        Assert.True(f.State.ForbidMovement);
        Assert.False(f.State.ForbidActions);
        Assert.False(f.State.ForceDisabled);
        Assert.Equal(f.Original.ActivePresets, f.State.ActivePresets);
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.False(f.State.ForbidMovement);
        Assert.Equal(new[] { "ForbidActions:False", "ForceDisabled:False", "Movement:False" }, f.Writes.Skip(3));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    public void OriginalActionAndForceDisableFlagsAreNeverEnabledByCleanup(int provider, bool forbidActions, bool forceDisabled)
    {
        var f = new Fixture((CrystalTowerMovementProvider)provider);
        f.State = f.State with { ForbidMovement = true, ForbidActions = forbidActions, ForceDisabled = forceDisabled,
            ActivePresets = forceDisabled ? new[] { string.Empty } : f.State.ActivePresets };
        var original = f.State;
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.True(f.State.ForbidMovement);
        Assert.Equal(original.ForbidActions, f.State.ForbidActions);
        Assert.Equal(original.ForceDisabled, f.State.ForceDisabled);
        Assert.Equal(original.ActivePresets, f.State.ActivePresets);
        Assert.DoesNotContain("Movement:False", f.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OriginallyEmptySelectionRestoresToNoneInsteadOfInventingADefaultPreset(int provider)
    {
        var f = new Fixture((CrystalTowerMovementProvider)provider);
        f.State = f.State with { ActivePresets = Array.Empty<string>() };
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        Assert.Empty(f.State.ActivePresets);
        Assert.False(f.State.ForceDisabled);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingOrUnreadableProviderCannotBeReportedAsAnActionPause(bool loaded, bool readable)
    {
        var f = new Fixture { Loaded = loaded, Readable = readable };
        Assert.False(f.Acquire());
        Assert.Empty(f.Writes);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("config")]
    [InlineData("runtime")]
    [InlineData("hints")]
    [InlineData("preset")]
    [InlineData("actions")]
    [InlineData("movement")]
    [InlineData("enabled")]
    [InlineData("modified")]
    public void ManualOrProviderChangesInvalidateWithoutReassertingOrRestoring(string change)
    {
        var f = new Fixture();
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        var owned = f.State;
        var writes = f.Writes.Count;
        f.State = change switch
        {
            "provider" => f.State with { Provider = new() },
            "config" => f.State with { AiConfig = new() },
            "runtime" => f.State with { Runtime = new() },
            "hints" => f.State with { Hints = new() },
            "preset" => f.State with { ForceDisabled = false, ActivePresets = new[] { "Manual" } },
            "actions" => f.State with { ForbidActions = false },
            "movement" => f.State with { ForbidMovement = false },
            "enabled" => f.State with { AiEnabled = false },
            _ => f.State with { LastModified = f.State.LastModified.AddTicks(1) },
        };
        Assert.False(f.Acquire());
        Assert.Equal(writes, f.Writes.Count);
        f.State = owned;
        f.AdvanceFrame();
        Assert.False(f.Acquire());
        Assert.False(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(writes, f.Writes.Count);
    }

    [Theory]
    [InlineData("cid")]
    [InlineData("entity")]
    [InlineData("alliance")]
    [InlineData("strategy")]
    public void OwnerChangesNeverRestoreFlagsIntoTheReplacementOwner(string change)
    {
        var f = new Fixture(CrystalTowerMovementProvider.BossModReborn);
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        var writes = f.Writes.Count;
        switch (change)
        {
            case "cid": f.Cid++; break;
            case "entity": f.EntityId++; break;
            case "alliance": f.Context = Context("B"); break;
            default: f.Strategy = CrystalTowerStrategyMode.Unsync; break;
        }
        Assert.False(f.Acquire());
        Assert.False(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.Equal(writes, f.Writes.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void RejectedOrThrowingSettersRetainConfirmedEffectsOnlyForGuardedCleanup(int failingSetting, bool throws)
    {
        var failing = (CrystalTowerActionSetting)failingSetting;
        var f = new Fixture();
        f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
        {
            f.ApplyAction(setting, value, names);
            if (setting == failing && value)
            {
                if (throws) throw new InvalidOperationException("Synthetic rejection after the native effect.");
                return false;
            }
            return true;
        };
        Assert.False(f.Acquire());
        var writes = f.Writes.Count;
        f.AdvanceFrame();
        Assert.False(f.Acquire());
        Assert.Equal(writes, f.Writes.Count);
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.False(f.State.ForbidMovement);
        Assert.False(f.State.ForbidActions);
        Assert.False(f.State.ForceDisabled);
        Assert.Equal(f.Original.ActivePresets, f.State.ActivePresets);
    }

    [Fact]
    public void IgnoredActionSetterIsNotRetriedOrMistakenForAConfirmedPause()
    {
        var f = new Fixture();
        f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
        {
            if (setting == CrystalTowerActionSetting.ForbidActions && value)
            { f.Writes.Add("IgnoredActions"); return false; }
            f.ApplyAction(setting, value, names);
            return true;
        };
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.False(f.Acquire());
        Assert.Single(f.Writes, write => write == "IgnoredActions");
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        Assert.False(f.State.ForceDisabled);
    }

    [Fact]
    public void VbmPartialListClearIsRetainedAndOriginalNamesCanStillBeRestored()
    {
        var f = new Fixture();
        f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
        {
            if (setting == CrystalTowerActionSetting.ForceDisabled && value)
            {
                f.Writes.Add("PartialClear");
                f.State = f.State with { ForceDisabled = false, ActivePresets = Array.Empty<string>() };
                return false;
            }
            f.ApplyAction(setting, value, names);
            return true;
        };
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.False(f.Acquire());
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        Assert.Equal(f.Original.ActivePresets, f.State.ActivePresets);
    }

    [Fact]
    public void UnexpectedSetterEffectsOrOwnerLossCannotAuthorizeASecondWrite()
    {
        foreach (var ownerLoss in new[] { false, true })
        {
            var f = new Fixture();
            f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
            {
                f.ApplyAction(setting, value, names);
                if (ownerLoss) f.Cid++;
                else f.State = f.State with { AiEnabled = false };
                return true;
            };
            Assert.False(f.Acquire());
            Assert.DoesNotContain("ForceDisabled:True", f.Writes);
            var writes = f.Writes.Count;
            Assert.False(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
            f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
            Assert.Equal(writes, f.Writes.Count);
        }
    }

    [Fact]
    public void VerifiedRestoreEffectsAllowMovementCleanupEvenWhenTheSetterRejectsItsAck()
    {
        var f = new Fixture(CrystalTowerMovementProvider.BossModReborn);
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
        { f.ApplyAction(setting, value, names); return false; };
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.False(f.State.ForbidMovement);
        Assert.Equal(f.Original.ActivePresets, f.State.ActivePresets);
    }

    [Fact]
    public void FailedActionRestorationPreventsPresetAndMovementReenable()
    {
        var f = new Fixture();
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        f.Execution.WriteCrystalTowerActionOverride = (_, _, _, _) => false;
        Assert.False(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: true);
        Assert.True(f.State.ForbidMovement);
        Assert.True(f.State.ForbidActions);
        Assert.True(f.State.ForceDisabled);
        Assert.DoesNotContain("Movement:False", f.Writes);
    }

    [Fact]
    public void FailedRestoreStaysBlockedWithoutRetryUntilExplicitTerminalDiscard()
    {
        var f = new Fixture();
        Assert.False(f.Acquire());
        f.AdvanceFrame();
        Assert.True(f.Acquire());
        f.Execution.WriteCrystalTowerActionOverride = (_, _, _, _) => false;
        var release = typeof(ExecutionService).GetMethod("ReleaseCrystalTowerPyretic", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var active = typeof(ExecutionService).GetField("crystalTowerPyreticActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)release.Invoke(f.Execution, new object?[] { f.Context, false })!);
        Assert.True((bool)active.GetValue(f.Execution)!);
        var writes = f.Writes.Count;
        f.Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
        { f.ApplyAction(setting, value, names); return true; };
        f.AdvanceFrame();
        Assert.False((bool)release.Invoke(f.Execution, new object?[] { f.Context, false })!);
        Assert.True((bool)active.GetValue(f.Execution)!);
        Assert.False(f.Acquire());
        Assert.Equal(writes, f.Writes.Count);
        Assert.False((bool)release.Invoke(f.Execution, new object?[] { f.Context, true })!);
        Assert.False((bool)active.GetValue(f.Execution)!);
        Assert.True(f.Execution.ReleaseCrystalTowerActionHold(f.Context));
        f.Execution.ReleaseCrystalTowerMovement(f.Context, restore: false);
        Assert.Equal(writes, f.Writes.Count);
        Assert.True(f.State.ForbidActions);
        Assert.True(f.State.ForceDisabled);
    }

    private static DutyContextSnapshot Context(string alliance = "A")
        => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 151, cfcId: 111, alliance: alliance);

    private sealed class Fixture
    {
        internal Fixture(CrystalTowerMovementProvider provider = CrystalTowerMovementProvider.BossMod)
        {
            var time = new DateTime(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);
            State = new(provider, new(), new(), new(), new(), new(),
                provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                false, provider == CrystalTowerMovementProvider.BossMod ? true : null, false, false,
                provider == CrystalTowerMovementProvider.BossMod ? new[] { "Healing", "Damage" } : new[] { "Damage" },
                time, time, true, false, false, false, false, true, false);
            Original = State;
            Execution = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Configuration());
            typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(Execution, OwnershipMode.OwnedStartInside);
            Execution.CrystalTowerMovementCharacterIdOverride = () => Cid;
            Execution.CrystalTowerMovementLocalEntityIdOverride = () => EntityId;
            Execution.CrystalTowerMovementStrategyOverride = _ => Strategy;
            Execution.ReadCrystalTowerMovementOverride = () => !Loaded ? new(false, null, string.Empty)
                : !Readable ? new(true, null, "Synthetic unreadable provider.") : new(true, State, string.Empty);
            Execution.WriteCrystalTowerMovementOverride = (_, forbid) =>
            {
                Writes.Add("Movement:" + forbid);
                State = State with { ForbidMovement = forbid,
                    LastModified = provider == CrystalTowerMovementProvider.BossMod ? State.LastModified.AddTicks(1) : State.LastModified };
                return true;
            };
            Execution.WriteCrystalTowerActionOverride = (_, setting, value, names) =>
            { ApplyAction(setting, value, names); return true; };
        }

        internal ExecutionService Execution { get; }
        internal CrystalTowerMovementSnapshot Original { get; }
        internal CrystalTowerMovementSnapshot State;
        internal DutyContextSnapshot Context = CrystalTowerActionHoldTests.Context();
        internal ulong Cid = 1;
        internal uint EntityId = 123;
        internal CrystalTowerStrategyMode Strategy = CrystalTowerStrategyMode.Sync;
        internal bool Loaded = true, Readable = true;
        internal List<string> Writes { get; } = [];
        internal bool Acquire() => Execution.TryAcquireCrystalTowerActionHold(Context, 123, CrystalTowerStrategyMode.Sync, out _);
        internal void AdvanceFrame() => State = State with { FrameTime = State.FrameTime.AddMilliseconds(16) };
        internal void ApplyAction(CrystalTowerActionSetting setting, bool value, IReadOnlyList<string> names)
        {
            Writes.Add(setting + ":" + value);
            State = setting == CrystalTowerActionSetting.ForbidActions
                ? State with { ForbidActions = value, LastModified = State.LastModified.AddTicks(1) }
                : State with { ForceDisabled = value, ActivePresets = value ? new[] { string.Empty } : names.ToArray() };
        }
    }
}
