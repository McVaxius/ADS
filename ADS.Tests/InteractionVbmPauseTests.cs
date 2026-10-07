using System.Reflection;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class InteractionVbmPauseTests
{
    [Fact]
    public void NativeTypeListConfigUsesThePublicGenericGetterForItsNode()
    {
        var node = new AiConfigContract();
        var root = new TypeListConfigRoot(node);

        var found = ExecutionService.ReadInteractionVbmAiConfig(root, typeof(AiConfigContract), typeof(AiConfigContract).Assembly);

        Assert.Same(typeof(AiConfigContract), Assert.Single(root.Nodes));
        Assert.Same(node, found);
        Assert.Equal(1, root.GetCalls);
    }

    [Fact]
    public void NativeGenericGetterDoesNotRequireANodesCollection()
    {
        var node = new AiConfigContract();

        Assert.Same(node, ExecutionService.ReadInteractionVbmAiConfig(new GetOnlyConfigRoot(node),
            typeof(AiConfigContract), typeof(AiConfigContract).Assembly));
    }

    [Theory]
    [InlineData("nodes-only")]
    [InlineData("non-generic")]
    [InlineData("requires-argument")]
    [InlineData("private")]
    public void UnsupportedConfigGetterContractsAreRejected(string shape)
    {
        var node = new AiConfigContract();
        object root = shape switch
        {
            "nodes-only" => new NodesOnlyConfigRoot(node),
            "non-generic" => new NonGenericConfigRoot(node),
            "requires-argument" => new ArgumentConfigRoot(node),
            _ => new PrivateConfigRoot(node),
        };

        Assert.Null(ExecutionService.ReadInteractionVbmAiConfig(root, typeof(AiConfigContract), typeof(AiConfigContract).Assembly));
    }

    [Fact]
    public void ConfigGetterReturningAnotherNodeTypeIsRejected()
        => Assert.Null(ExecutionService.ReadInteractionVbmAiConfig(new WrongNodeConfigRoot(),
            typeof(AiConfigContract), typeof(AiConfigContract).Assembly));

    [Fact]
    public void ConfigGetterReturningASubclassIsRejected()
        => Assert.Null(ExecutionService.ReadInteractionVbmAiConfig(new GetOnlyConfigRoot(new DerivedAiConfigContract()),
            typeof(AiConfigContract), typeof(AiConfigContract).Assembly));

    [Fact]
    public void ConfigRootOutsideTheSelectedProviderAssemblyIsRejected()
        => Assert.Null(ExecutionService.ReadInteractionVbmAiConfig(new object(), typeof(AiConfigContract), typeof(AiConfigContract).Assembly));

    [Fact]
    public void ConfigNodeTypeOutsideTheSelectedProviderAssemblyIsRejected()
    {
        var root = new TypeListConfigRoot(new AiConfigContract());

        Assert.Null(ExecutionService.ReadInteractionVbmAiConfig(root, typeof(string), typeof(AiConfigContract).Assembly));
        Assert.Equal(0, root.GetCalls);
    }

    [Fact]
    public void PauseDisablesAiAndPresetsAndRestoresAiBeforeExactOriginalOrder()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Second", "VBM Multibox", "first");
        var original = fixture.Names.ToArray();

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.True(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.False(fixture.AiEnabled);
        Assert.True(fixture.ForceDisabled);
        Assert.Equal(new[] { "ai:False", "presets:True:" }, fixture.Writes);

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.Equal(2, fixture.Writes.Count);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.Equal(original, fixture.Names);
        Assert.Equal(new[] { "ai:False", "presets:True:", "ai:True", "presets:False:Second|VBM Multibox|first" }, fixture.Writes);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Fact]
    public void ActivePresetsArePausedEvenWhenAiWasAlreadyOff()
    {
        var fixture = new Fixture(aiEnabled: false, forceDisabled: false, "Rotation");

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.True(fixture.ForceDisabled);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.False(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.Equal(new[] { "Rotation" }, fixture.Names);
        Assert.Equal(new[] { "presets:True:", "presets:False:Rotation" }, fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitiallyOffKeepsAiOffAndPublishesTheInteractionHold(bool forceDisabled)
    {
        var fixture = new Fixture(aiEnabled: false, forceDisabled);

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.True(fixture.Execution.IsInteractionVbmPauseActive);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.False(fixture.AiEnabled);
        Assert.Equal(forceDisabled, fixture.ForceDisabled);
        Assert.Equal(forceDisabled ? Array.Empty<string>() : new[] { "presets:True:", "presets:False:" }, fixture.Writes);
    }

    [Fact]
    public void NativeForceDisableSentinelIsNotReplayedAsASelectablePreset()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation", "VBM Multibox");
        fixture.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, names) =>
        {
            fixture.Writes.Add($"presets:{disabled}");
            fixture.ForceDisabled = disabled;
            fixture.Names.Clear();
            fixture.Names.AddRange(disabled ? new[] { string.Empty } : names);
            return true;
        };

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.Equal(new[] { string.Empty }, fixture.Names);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.Equal(new[] { "Rotation", "VBM Multibox" }, fixture.Names);
    }

    [Fact]
    public void OriginallyForceDisabledSentinelKeepsItsOriginalStateWithoutWrites()
    {
        var fixture = new Fixture(aiEnabled: false, forceDisabled: true, string.Empty);

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.True(fixture.Execution.IsInteractionVbmPauseActive);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.False(fixture.AiEnabled);
        Assert.True(fixture.ForceDisabled);
        Assert.Equal(new[] { string.Empty }, fixture.Names);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public void OriginallyForceDisabledAiIsRestoredWithoutEnablingAutorotation()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: true);

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.True(fixture.AiEnabled);
        Assert.True(fixture.ForceDisabled);
        Assert.Empty(fixture.Names);
        Assert.Equal(new[] { "ai:False", "ai:True" }, fixture.Writes);
    }

    [Fact]
    public void DeferredNativeMultiboxRemovalKeepsThePauseOwnedAndRestoresTheExactSnapshot()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation", "VBM Multibox");
        fixture.Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
        {
            fixture.Writes.Add($"ai:{enabled}");
            fixture.AiEnabled = enabled;
            return true;
        };
        fixture.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, saved) =>
        {
            fixture.Writes.Add($"presets:{disabled}:{string.Join("|", saved)}");
            fixture.ForceDisabled = disabled;
            if (!disabled)
            {
                fixture.Names.Clear();
                fixture.Names.AddRange(saved);
            }
            return true;
        };
        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        fixture.Names.RemoveAll(name => name == "VBM Multibox");

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        Assert.Equal(2, fixture.Writes.Count);
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.Equal(new[] { "Rotation", "VBM Multibox" }, fixture.Names);
        Assert.Equal(new[] { "ai:False", "presets:True:", "ai:True", "presets:False:Rotation|VBM Multibox" }, fixture.Writes);
    }

    [Theory]
    [InlineData("ai")]
    [InlineData("force")]
    [InlineData("presets")]
    public void ManualRuntimeChangesArePreserved(string changed)
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        if (changed == "ai") fixture.AiEnabled = true;
        if (changed == "force") fixture.ForceDisabled = false;
        if (changed == "presets") fixture.Names.Add("Manual");
        var before = (fixture.AiEnabled, fixture.ForceDisabled, fixture.Names.ToArray());
        fixture.Writes.Clear();

        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.Empty(fixture.Writes);
        Assert.Equal(before.AiEnabled, fixture.AiEnabled);
        Assert.Equal(before.ForceDisabled, fixture.ForceDisabled);
        Assert.Equal(before.Item3, fixture.Names);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("ai-config")]
    [InlineData("preset-runtime")]
    [InlineData("rotation-database")]
    public void RuntimeReplacementNeverReceivesTheDepartedSnapshot(string changed)
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        switch (changed)
        {
            case "provider": fixture.Provider = new(); break;
            case "ai-config": fixture.AiConfig = new(); break;
            case "preset-runtime": fixture.PresetRuntime = new(); break;
            case "rotation-database": fixture.RotationDatabase = new(); break;
        }
        fixture.Writes.Clear();

        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.Empty(fixture.Writes);
        Assert.False(fixture.AiEnabled);
        Assert.True(fixture.ForceDisabled);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("territory")]
    [InlineData("duty")]
    [InlineData("mode")]
    [InlineData("logout")]
    [InlineData("disabled")]
    [InlineData("transition")]
    [InlineData("discard")]
    public void OwnerAndLifecycleBoundariesDoNotEnableThePreviousRuntime(string changed)
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        var context = changed switch
        {
            "territory" => TestDutyContextFactory.Create(territoryId: 778),
            "duty" => TestDutyContextFactory.Create(cfcId: 889),
            "logout" => TestDutyContextFactory.Create(loggedIn: false),
            "disabled" => TestDutyContextFactory.Create(pluginEnabled: false),
            "transition" => TestDutyContextFactory.Create(betweenAreas: true),
            _ => fixture.Context,
        };
        if (changed == "character") fixture.CharacterId++;
        if (changed == "mode") fixture.SetMode(OwnershipMode.Observing);
        fixture.Writes.Clear();

        fixture.Execution.ReleaseInteractionVbmPause(context, allowRestore: changed != "discard");

        Assert.Empty(fixture.Writes);
        Assert.False(fixture.AiEnabled);
        Assert.True(fixture.ForceDisabled);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Fact]
    public void LeavingModeCanPauseAndRestoreItsTreasureSweepInteraction()
    {
        var fixture = new Fixture(aiEnabled: false, forceDisabled: false, "Rotation");
        fixture.SetMode(OwnershipMode.Leaving);

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.Equal(new[] { "Rotation" }, fixture.Names);
        Assert.False(fixture.ForceDisabled);
    }

    [Fact]
    public void LoadedButUnreadableStateBlocksBeforeAnySetter()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation") { Readable = false };

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.Empty(fixture.Writes);
        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
    }

    [Fact]
    public void AbsentVbmDoesNotChangeAnotherRotationProvider()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Other provider") { Loaded = false };

        Assert.True(fixture.Execution.TryPauseInteractionVbm(fixture.Context));
        fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: true);

        Assert.Empty(fixture.Writes);
        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
    }

    [Fact]
    public void RejectedPresetPauseRestoresTheConfirmedAiChangeAndBlocksDispatch()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "VBM Multibox", "Rotation") { RejectPresetPause = true };

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.Equal(new[] { "VBM Multibox", "Rotation" }, fixture.Names);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Fact]
    public void UnconfirmedAiPauseBlocksDispatch()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation") { IgnoreAiWrites = true };

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.True(fixture.AiEnabled);
        Assert.False(fixture.ForceDisabled);
        Assert.DoesNotContain(fixture.Writes, write => write.StartsWith("presets:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadbackLossAfterAiWriteDoesNotAttemptBlindRestoration()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        fixture.AfterAiWrite = () => fixture.Readable = false;

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.Equal(new[] { "ai:False" }, fixture.Writes);
        Assert.False(fixture.AiEnabled);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Fact]
    public void UnrelatedPresetEditDuringAiSetterIsNotClaimedForRollback()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        fixture.AfterAiWrite = () => fixture.Names.Add("Manual");

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.Equal(new[] { "ai:False" }, fixture.Writes);
        Assert.Equal(new[] { "Rotation", "Manual" }, fixture.Names);
        Assert.False(fixture.AiEnabled);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    [Fact]
    public void StopDuringSetterCannotContinuePauseOrRestoreIntoTheStoppedSession()
    {
        var fixture = new Fixture(aiEnabled: true, forceDisabled: false, "Rotation");
        fixture.AfterAiWrite = () =>
        {
            fixture.Execution.ReleaseInteractionVbmPause(fixture.Context, allowRestore: false);
            fixture.SetMode(OwnershipMode.Observing);
        };

        Assert.False(fixture.Execution.TryPauseInteractionVbm(fixture.Context));

        Assert.Equal(new[] { "ai:False" }, fixture.Writes);
        Assert.False(fixture.AiEnabled);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
    }

    private class AiConfigContract { }
    private sealed class DerivedAiConfigContract : AiConfigContract { }
    private sealed class TypeListConfigRoot(AiConfigContract node)
    {
        public IEnumerable<Type> Nodes => new[] { typeof(AiConfigContract) };
        public int GetCalls { get; private set; }
        public T Get<T>()
        {
            GetCalls++;
            return (T)(object)node;
        }
    }
    private sealed class GetOnlyConfigRoot(AiConfigContract node)
    {
        public T Get<T>() => (T)(object)node;
    }
    private sealed class NodesOnlyConfigRoot(AiConfigContract node)
    {
        public IEnumerable<object> Nodes => new object[] { node };
    }
    private sealed class NonGenericConfigRoot(AiConfigContract node)
    {
        public AiConfigContract Get() => node;
    }
    private sealed class ArgumentConfigRoot(AiConfigContract node)
    {
        public T Get<T>(int argument) => (T)(object)node;
    }
    private sealed class PrivateConfigRoot(AiConfigContract node)
    {
        private T Get<T>() => (T)(object)node;
    }
    private sealed class WrongNodeConfigRoot
    {
        public object Get<T>() => new object();
    }

    private sealed class Fixture
    {
        internal Fixture(bool aiEnabled, bool forceDisabled, params string[] names)
        {
            AiEnabled = aiEnabled;
            ForceDisabled = forceDisabled;
            Names.AddRange(names);
            Execution = new ExecutionService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Configuration());
            SetMode(OwnershipMode.OwnedStartInside);
            Execution.InteractionVbmCharacterIdOverride = () => CharacterId;
            Execution.ReadInteractionVbmOverride = () => !Loaded
                ? new(false, null, "VBM is not loaded.")
                : !Readable ? new(true, null, "VBM state is unavailable.")
                : new(true, new(Provider, AiConfig, PresetRuntime, RotationDatabase, AiEnabled, ForceDisabled, Names), string.Empty);
            Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
            {
                Writes.Add($"ai:{enabled}");
                if (!IgnoreAiWrites)
                {
                    AiEnabled = enabled;
                    Names.RemoveAll(name => name == "VBM Multibox");
                    if (enabled && !ForceDisabled)
                    {
                        // The AI setter can rebuild hidden runtime entries;
                        // exact preset restoration must happen afterward.
                        Names.Add("VBM Multibox");
                    }
                }
                AfterAiWrite?.Invoke();
                return true;
            };
            Execution.WriteInteractionVbmPresetsOverride = (_, disabled, saved) =>
            {
                Writes.Add($"presets:{disabled}:{string.Join("|", saved)}");
                if (disabled && RejectPresetPause)
                    return false;
                ForceDisabled = disabled;
                // Mutating the same live list proves the original snapshot is
                // a value copy rather than an alias of the provider collection.
                Names.Clear();
                Names.AddRange(saved);
                return true;
            };
        }

        internal ExecutionService Execution { get; }
        internal DutyContextSnapshot Context { get; } = TestDutyContextFactory.Create();
        internal object Provider = new();
        internal object AiConfig = new();
        internal object PresetRuntime = new();
        internal object RotationDatabase = new();
        internal ulong CharacterId = 1;
        internal bool AiEnabled, ForceDisabled;
        internal bool Loaded = true, Readable = true, RejectPresetPause, IgnoreAiWrites;
        internal Action? AfterAiWrite;
        internal List<string> Names { get; } = [];
        internal List<string> Writes { get; } = [];

        internal void SetMode(OwnershipMode mode)
            => typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(Execution, mode);
    }
}
