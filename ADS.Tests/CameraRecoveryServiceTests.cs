using ADS.Services;

namespace ADS.Tests;

public sealed class CameraRecoveryServiceTests
{
    [Theory]
    [InlineData(0, true)] // FirstPerson
    [InlineData(3, true)] // LockonFirstPerson
    [InlineData(4, true)] // FirstPersonUnk
    [InlineData(1, false)] // ThirdPersonLegacy
    [InlineData(2, false)] // ThirdPersonFixed
    [InlineData(5, false)] // LockonThirdPerson
    public void FirstPersonModesAreExplicit(int mode, bool expected)
        => Assert.Equal(expected, DalamudCameraRecoveryRuntime.IsFirstPersonControlModeValue(mode));

    [Fact]
    public void IdleCameraHasPriorityWhenBothStatesAreActive()
    {
        var runtime = new FakeRuntime { State = new CameraRecoveryState(true, true) };
        var service = new CameraRecoveryService(runtime, new FakeClock());

        service.Update(TestDutyContextFactory.Create(), executionOwned: true);

        Assert.Equal(1, runtime.StopIdleCalls);
        Assert.Equal(0, runtime.TapCalls);
    }

    [Theory]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, true, false, true)]
    public void UnsafeOrNonOwnedStatesDoNotCorrect(
        bool enabled,
        bool loggedIn,
        bool inDuty,
        bool transition,
        bool occupied)
    {
        var runtime = new FakeRuntime { State = new CameraRecoveryState(true, true) };
        var service = new CameraRecoveryService(runtime, new FakeClock());
        var context = TestDutyContextFactory.Create(
            pluginEnabled: enabled,
            loggedIn: loggedIn,
            inDuty: inDuty,
            betweenAreas: transition,
            occupied: occupied);

        service.Update(context, executionOwned: true);

        Assert.Equal(0, runtime.StopIdleCalls + runtime.TapCalls);
    }

    [Fact]
    public void OwnershipIsRequired()
    {
        var runtime = new FakeRuntime { State = new CameraRecoveryState(true, false) };
        var service = new CameraRecoveryService(runtime, new FakeClock());

        service.Update(TestDutyContextFactory.Create(), executionOwned: false);

        Assert.Equal(0, runtime.TapCalls);
    }

    [Fact]
    public void FailedAttemptsConsumeTheSharedTenSecondCooldown()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            State = new CameraRecoveryState(true, false),
            TapSucceeds = false,
        };
        var service = new CameraRecoveryService(runtime, clock);
        var context = TestDutyContextFactory.Create();

        service.Update(context, executionOwned: true);
        clock.UtcNowValue = clock.UtcNowValue.AddSeconds(9.999);
        service.Update(context, executionOwned: true);
        clock.UtcNowValue = clock.UtcNowValue.AddMilliseconds(1);
        service.Update(context, executionOwned: true);

        Assert.Equal(2, runtime.TapCalls);
    }

    [Fact]
    public void InjectedKeyReleasesOnNextTickAndBeforeGateChecks()
    {
        var runtime = new FakeRuntime { State = new CameraRecoveryState(true, false) };
        var service = new CameraRecoveryService(runtime, new FakeClock());

        service.Update(TestDutyContextFactory.Create(), executionOwned: true);
        Assert.True(runtime.Injected);

        service.Update(TestDutyContextFactory.Create(pluginEnabled: false), executionOwned: false);

        Assert.False(runtime.Injected);
        Assert.Equal(1, runtime.ReleaseCalls);
    }

    [Fact]
    public void DisposeReleasesOwnedInput()
    {
        var runtime = new FakeRuntime { State = new CameraRecoveryState(true, false) };
        var service = new CameraRecoveryService(runtime, new FakeClock());
        service.Update(TestDutyContextFactory.Create(), executionOwned: true);

        service.Dispose();

        Assert.False(runtime.Injected);
        Assert.Equal(1, runtime.ReleaseCalls);
    }

    [Fact]
    public void RepeatedUnavailableReasonLogsOnce()
    {
        var warnings = new List<string>();
        var runtime = new FakeRuntime { ReadSucceeds = false, Failure = "camera pointer missing" };
        var service = new CameraRecoveryService(runtime, new FakeClock(), warnings.Add);

        service.Update(TestDutyContextFactory.Create(), executionOwned: true);
        service.Update(TestDutyContextFactory.Create(), executionOwned: true);

        Assert.Single(warnings);
    }

    private sealed class FakeClock : ICameraRecoveryClock
    {
        public DateTime UtcNowValue { get; set; } = new(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => UtcNowValue;
    }

    private sealed class FakeRuntime : ICameraRecoveryRuntime
    {
        public CameraRecoveryState State { get; set; }
        public bool ReadSucceeds { get; set; } = true;
        public bool StopSucceeds { get; set; } = true;
        public bool TapSucceeds { get; set; } = true;
        public string Failure { get; set; } = "unavailable";
        public int StopIdleCalls { get; private set; }
        public int TapCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public bool Injected { get; private set; }

        public bool TryReadState(out CameraRecoveryState state, out string unavailableReason)
        {
            state = State;
            unavailableReason = ReadSucceeds ? string.Empty : Failure;
            return ReadSucceeds;
        }

        public bool TryStopIdleCamera(out string unavailableReason)
        {
            StopIdleCalls++;
            unavailableReason = StopSucceeds ? string.Empty : Failure;
            return StopSucceeds;
        }

        public bool TryTapCameraMode(out string unavailableReason)
        {
            TapCalls++;
            unavailableReason = TapSucceeds ? string.Empty : Failure;
            Injected = TapSucceeds;
            return TapSucceeds;
        }

        public void ReleaseInjectedKey(string reason)
        {
            if (!Injected)
                return;

            Injected = false;
            ReleaseCalls++;
        }
    }
}
