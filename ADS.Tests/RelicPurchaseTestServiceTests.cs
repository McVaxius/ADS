using ADS.Models;
using ADS.Services;
using Xunit;

namespace ADS.Tests;

public sealed class RelicPurchaseTestServiceTests
{
    [Fact]
    public void DefaultIsOptOutAndSelectionOnlyArmsNextLoad()
    {
        var state = new RelicPurchaseTestState();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.False(test.RunRemainingNow());
        Assert.True(test.SetSelected(true));
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.Equal(runtime.CharacterId, state.CharacterId);

        var reloaded = Create(state, runtime);
        reloaded.Update();
        Assert.Single(runtime.Starts);
        Assert.False(reloaded.IsReloadPending);
    }

    [Fact]
    public void WaitsForReadinessThenConsumesOnceEvenAcrossLogins()
    {
        var runtime = new FakeRuntime { IsReady = false, RejectStarts = true };
        var test = Create(Armed(), runtime);
        Tick(test);
        Assert.True(test.IsReloadPending);
        Assert.Empty(runtime.Starts);
        runtime.IsReady = true;
        Tick(test);
        Assert.Equal(13, runtime.Starts.Count);
        Assert.Equal(13, runtime.Starts.Distinct().Count());
        Assert.DoesNotContain(7884u, runtime.Starts);
        runtime.IsReady = false;
        Tick(test);
        runtime.IsReady = true;
        Tick(test);
        Assert.Equal(13, runtime.Starts.Count);
    }

    [Fact]
    public void WrongCharacterCannotDispatchOrRearmOnLogin()
    {
        var runtime = new FakeRuntime { CharacterId = 2 };
        var state = Armed();
        var test = Create(state, runtime);
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.Equal(0, runtime.Cleanups);
        Assert.False(test.SetSelected(true));
        Assert.False(test.RunRemainingNow());
        runtime.CharacterId = 1;
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.True(test.RunRemainingNow());
        test.Update();
        Assert.Single(runtime.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrUncheckCancelsPendingWithoutUiOrLoginRearming(bool uncheck)
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        if (uncheck) test.SetSelected(false); else test.Stop();
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.Equal(!uncheck, state.SelectedTask != null);
    }

    [Fact]
    public void UncheckingActivePurchaseCancelsButRetainsInterruptedEvidence()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        test.Update();
        runtime.Submit();
        Assert.NotNull(state.InterruptedPurchase);
        test.SetSelected(false);
        Tick(test);
        Assert.Equal(1, runtime.Cancels);
        Assert.Single(runtime.Starts);
        Assert.NotNull(state.InterruptedPurchase);
        Assert.Empty(state.CompletedItems);
        Assert.False(test.ResetProgress());
    }

    [Fact]
    public void ChangingCharacterCancelsBeforeNextPurchase()
    {
        var runtime = new FakeRuntime();
        var test = Create(Armed(), runtime);
        test.Update();
        runtime.CharacterId = 2;
        test.Update();
        Assert.False(test.IsRunning);
        Assert.Equal(1, runtime.Cancels);
        Assert.Single(runtime.Starts);
    }

    [Fact]
    public void SaveBeforeSubmissionAndVerifiedSuccessSurviveReload()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var saved = new List<string>();
        var test = Create(state, runtime, () => saved.Add(System.Text.Json.JsonSerializer.Serialize(state)));
        test.Update();
        runtime.Submit();
        Assert.Empty(state.CompletedItems);
        Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<RelicPurchaseTestState>(saved.Single())!.InterruptedPurchase);
        runtime.Verify();
        Assert.Contains(runtime.Starts[0], state.CompletedItems);
        Assert.Null(state.InterruptedPurchase);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RelicPurchaseTestState>(saved.Last())!;
        var nextRuntime = new FakeRuntime();
        Create(restored, nextRuntime).Update();
        Assert.Equal(RelicPurchaseTestCatalog.Items[1].ItemId, nextRuntime.Starts.Single());
    }

    [Fact]
    public void RejectedStartCannotInheritEarlierSuccessfulStatus()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        test.Update();
        runtime.Submit();
        runtime.Verify();
        runtime.RejectStarts = true;
        Tick(test);
        Assert.Single(state.CompletedItems);
        Assert.Equal(13, runtime.Starts.Count);
        Assert.Contains("rejected", test.ItemStatus(RelicPurchaseTestCatalog.Items[1].ItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedTestIsNoOpUntilExplicitReset(bool hasRetiredSuccess)
    {
        var state = Armed();
        // The 13 saved direct-shop successes from the previous catalog.
        state.CompletedItems.UnionWith([6267u, 6268u, 7885u, 9540u, 13582u, 13584u, 13586u,
            13588u, 14899u, 15840u, 16064u, 16933u, 16934u]);
        if (hasRetiredSuccess)
            state.CompletedItems.Add(7884);
        var saved = System.Text.Json.JsonSerializer.Serialize(state);
        var runtime = new FakeRuntime();
        var saves = 0;
        var test = Create(state, runtime, () => saves++);
        Tick(test);
        Assert.Contains("Already complete", test.Status);
        Assert.False(test.IsReloadPending);
        Assert.True(test.RunRemainingNow());
        Tick(test);
        Assert.Contains("Already complete", test.Status);
        Assert.False(test.IsRunning);
        Assert.Empty(runtime.Starts);
        Assert.Equal(0, runtime.Cleanups);
        Assert.Equal(0, runtime.Cancels);
        Assert.Equal(0, saves);
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Serialize(state));
        Assert.True(test.ResetProgress());
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.True(test.RunRemainingNow());
        test.Update();
        Assert.Single(runtime.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassVerifiesThirteenDistinctMaterialsRegardlessOfRetiredSuccess(bool hasRetiredSuccess)
    {
        var state = Armed();
        if (hasRetiredSuccess)
            state.CompletedItems.Add(7884);
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        for (var i = 0; i < 13; i++)
        {
            Tick(test);
            Assert.True(runtime.PurchaseStatus.Running);
            runtime.Submit();
            runtime.Verify();
        }
        Tick(test);
        Assert.Equal(13, runtime.Starts.Count);
        Assert.Equal(13, runtime.Starts.Distinct().Count());
        Assert.DoesNotContain(7884u, runtime.Starts);
        Assert.Equal(hasRetiredSuccess ? 14 : 13, state.CompletedItems.Count);
        Assert.Null(state.InterruptedPurchase);
        Assert.False(test.IsRunning);
        Assert.Contains("Complete: all 13", test.Status);
    }

    [Fact]
    public void InterruptedExactDeltasReconcileBeforeAnyRetry()
    {
        var state = Interrupted();
        var runtime = new FakeRuntime { Count = 8, Poetics = 850 };
        var test = Create(state, runtime);
        test.Update();
        Assert.Contains(6267u, state.CompletedItems);
        Assert.Null(state.InterruptedPurchase);
        Assert.DoesNotContain(6267u, runtime.Starts);
        Assert.Equal(6268u, runtime.Starts.Single());
    }

    [Theory]
    [InlineData(7, 1000)] // No visible change still cannot prove the callback was not processed.
    [InlineData(8, 1000)]
    [InlineData(7, 850)]
    [InlineData(8, 849)]
    [InlineData(9, 850)]
    public void UncertainInterruptionBlocksPurchasesAndReset(long count, long poetics)
    {
        var state = Interrupted();
        var runtime = new FakeRuntime { Count = count, Poetics = poetics };
        var test = Create(state, runtime);
        Tick(test);
        Assert.Empty(runtime.Starts);
        Assert.Empty(state.CompletedItems);
        Assert.NotNull(state.InterruptedPurchase);
        Assert.False(test.ResetProgress());
        Assert.False(test.IsRunning);
    }

    [Fact]
    public void FailedSubmittedPurchaseStopsPassInsteadOfDuplicating()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        test.Update();
        runtime.Submit();
        runtime.Fail();
        Tick(test);
        Assert.Single(runtime.Starts);
        Assert.NotNull(state.InterruptedPurchase);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public void FailureBeforeSubmissionContinuesOnlyAfterSuccessfulCleanup()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var clock = new FakeClock();
        var test = Create(state, runtime, clock: clock);
        test.Update();
        runtime.Fail();
        runtime.CleanupBlocker = "path still running";
        test.Update();
        clock.UtcNow += TimeSpan.FromSeconds(11);
        Tick(test);
        Assert.False(test.IsRunning);
        Assert.Single(runtime.Starts);
        Assert.Contains("path still running", test.Status);
    }

    [Fact]
    public void ReturningParentMenuMustFinishCleanupBeforeNextPurchase()
    {
        var runtime = new FakeRuntime { CleanupBlocker = "parent menu", CleanupUpdatesUntilClosed = 2 };
        var test = Create(Armed(), runtime);
        test.Update();
        Assert.Empty(runtime.Starts);
        test.Update();
        Assert.Single(runtime.Starts);
        test.Stop();
        var cleanupUpdates = runtime.CleanupUpdates;
        Tick(test);
        Assert.Equal(cleanupUpdates, runtime.CleanupUpdates);
    }

    [Fact]
    public void AvailabilityIsRecheckedAfterCleanup()
    {
        var runtime = new FakeRuntime { UnavailableReason = "missing dependency" };
        var test = Create(Armed(), runtime);
        Tick(test);
        Assert.Equal(1, runtime.Cleanups);
        Assert.Empty(runtime.Starts);
        Assert.Contains("missing dependency", test.Status);
    }

    [Fact]
    public void CheckpointMustUseOnlyPoeticsAndBoundCurrentItem()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        test.Update();
        Assert.Throws<InvalidOperationException>(() => runtime.Before!(new(6267, 1, 7,
            new ShopCurrencyIdentity(ShopCurrencyKind.Gil, 1), 1000, 15)));
        Assert.Null(state.InterruptedPurchase);
        Assert.Equal(13, RelicPurchaseTestCatalog.Items.Select(item => item.ItemId).Distinct().Count());
        Assert.DoesNotContain(RelicPurchaseTestCatalog.Items, item => item.ItemId == 7884);
    }

    [Fact]
    public void LostReadinessPreventsSavingOrSendingPurchase()
    {
        var state = Armed();
        var runtime = new FakeRuntime();
        var test = Create(state, runtime);
        test.Update();
        runtime.IsReady = false;
        Assert.Throws<InvalidOperationException>(runtime.Submit);
        Assert.Null(state.InterruptedPurchase);
        Assert.False(runtime.HasPurchaseSubmission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupExceptionEndsAutomaticOrManualAttempt(bool manual)
    {
        var runtime = new FakeRuntime { ThrowCleanup = true };
        var test = Create(Armed(), runtime);
        if (manual) Assert.False(test.RunRemainingNow()); else test.Update();
        runtime.ThrowCleanup = false;
        Tick(test);
        Assert.False(test.IsRunning);
        Assert.False(test.IsReloadPending);
        Assert.Empty(runtime.Starts);
    }

    private static RelicPurchaseTestState Armed() => new() { SelectedTask = RelicPurchaseTestService.TaskId, CharacterId = 1 };
    private static RelicPurchaseTestState Interrupted()
    {
        var state = Armed();
        state.InterruptedPurchase = new(6267, 7, 1000, 150);
        return state;
    }
    private static RelicPurchaseTestService Create(RelicPurchaseTestState state, FakeRuntime runtime, Action? save = null, FakeClock? clock = null)
        => new(state, runtime, save ?? (() => { }), _ => { }, clock ?? new FakeClock());
    private static void Tick(RelicPurchaseTestService test) { for (var i = 0; i < 80; i++) test.Update(); }
    private sealed class FakeClock : IShopPurchaseClock { public DateTime UtcNow { get; set; } = DateTime.UtcNow; }

    private sealed class FakeRuntime : IRelicPurchaseTestRuntime
    {
        public ulong CharacterId { get; set; } = 1;
        public bool IsReady { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public ShopPurchaseStatusSnapshot PurchaseStatus { get; private set; } = new(false, false, null, "idle", 0, "", 0, 0, 0, null, [], null, "", "", "", "", null);
        public bool HasPurchaseSubmission { get; private set; }
        public List<uint> Starts { get; } = [];
        public bool RejectStarts { get; set; }
        public int Cleanups { get; private set; }
        public int Cancels { get; private set; }
        public string? CleanupBlocker { get; set; }
        public bool ThrowCleanup { get; set; }
        public int CleanupUpdatesUntilClosed { get; set; }
        public int CleanupUpdates { get; private set; }
        public long Count { get; set; } = 7;
        public long Poetics { get; set; } = 1000;
        public Action<ShopPurchaseCheckpoint>? Before;
        private Action<ShopPurchaseCheckpoint>? verified;
        private ShopPurchaseCheckpoint? checkpoint;
        public void BeginCleanup()
        {
            Cleanups++;
            if (ThrowCleanup) throw new InvalidOperationException("Fixture cleanup failed");
        }
        public void ContinueCleanup()
        {
            CleanupUpdates++;
            if (CleanupUpdatesUntilClosed > 0 && --CleanupUpdatesUntilClosed == 0)
                CleanupBlocker = null;
        }
        public bool Start(uint itemId, Action<ShopPurchaseCheckpoint> beforeSubmit, Action<ShopPurchaseCheckpoint> onVerified)
        {
            Starts.Add(itemId);
            if (RejectStarts)
            {
                PurchaseStatus = PurchaseStatus with { LastStartError = "Fixture start rejected" };
                return false;
            }
            Before = beforeSubmit;
            verified = onVerified;
            HasPurchaseSubmission = false;
            PurchaseStatus = PurchaseStatus with { ItemId = itemId, Running = true, Done = false, Succeeded = null };
            return true;
        }
        public void Submit()
        {
            checkpoint = new(PurchaseStatus.ItemId, 1, Count, RelicPurchaseTestService.Currency, Poetics, 150);
            Before!(checkpoint);
            HasPurchaseSubmission = true;
        }
        public void Verify()
        {
            Count++;
            Poetics -= 150;
            verified!(checkpoint!);
            PurchaseStatus = PurchaseStatus with { Running = false, Done = true, Succeeded = true, AcquiredQuantity = 1 };
        }
        public void Fail() => PurchaseStatus = PurchaseStatus with { Running = false, Done = true, Succeeded = false, FailureMessage = "Fixture failure" };
        public void Cancel() { Cancels++; Fail(); }
        public long ItemCount(uint itemId) => Count;
    }
}
