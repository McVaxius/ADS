using ADS.Models;

namespace ADS.Services;

internal interface IRelicPurchaseTestRuntime
{
    ulong CharacterId { get; }
    bool IsReady { get; }
    string? UnavailableReason { get; }
    ShopPurchaseStatusSnapshot PurchaseStatus { get; }
    bool HasPurchaseSubmission { get; }
    void BeginCleanup();
    void ContinueCleanup() { }
    string? CleanupBlocker { get; }
    bool Start(uint itemId, Action<ShopPurchaseCheckpoint> beforeSubmit, Action<ShopPurchaseCheckpoint> verified);
    void Cancel();
    long ItemCount(uint itemId);
    long Poetics { get; }
}

internal sealed class RelicPurchaseTestService(
    RelicPurchaseTestState state,
    IRelicPurchaseTestRuntime runtime,
    Action save,
    Action<string> diagnostic,
    IShopPurchaseClock clock)
{
    internal const string TaskId = "arr-anima-poetics-one-each";
    internal static readonly ShopCurrencyIdentity Currency = new(ShopCurrencyKind.Tomestone, 28);
    private readonly ulong loadedCharacterId = state.CharacterId;
    private bool pendingReload = state.SelectedTask == TaskId;
    private readonly HashSet<uint> attempted = [];
    private readonly Dictionary<uint, string> failures = [];
    private uint activeItem;
    private bool cleaning;
    private DateTime cleanupStarted;

    public bool IsRunning { get; private set; }
    public bool IsReloadPending => pendingReload;
    public bool IsSelected => state.SelectedTask == TaskId;
    public bool IsBoundCharacter => runtime.CharacterId != 0 && state.CharacterId == runtime.CharacterId;
    public string Status { get; private set; } = state.SelectedTask == TaskId ? "Waiting for character readiness after reload." : "Not armed.";
    public string CurrentAction => activeItem != 0 && runtime.PurchaseStatus.Running ? runtime.PurchaseStatus.StatusMessage : Status;
    public string ItemStatus(uint itemId) => state.CompletedItems.Contains(itemId) ? "Complete: one additional item verified."
        : state.InterruptedPurchase?.ItemId == itemId ? "Unresolved purchase: reconciliation required."
        : failures.GetValueOrDefault(itemId, activeItem == itemId ? CurrentAction : "Remaining");

    public bool SetSelected(bool selected)
    {
        if (!selected)
        {
            Stop("Reload attempt disabled.");
            state.SelectedTask = null;
        }
        else
        {
            if (!runtime.IsReady || runtime.CharacterId == 0)
                return ReportFailure("Log in on the character that will run the test before arming it.");
            if (state.CharacterId != 0 && !IsBoundCharacter)
                return ReportFailure("The test belongs to another character. Reset its progress before binding a different character.");
            state.CharacterId = runtime.CharacterId;
            state.SelectedTask = TaskId;
            pendingReload = false; // A UI selection only arms a future plugin load.
            Report("Armed for the next reload on this character. Run remaining now can start a pass immediately.");
        }
        save();
        return true;
    }

    public bool ResetProgress()
    {
        if (IsRunning || runtime.PurchaseStatus.Running)
            return ReportFailure("Stop the current purchase and wait for cleanup before resetting progress.");
        if (state.InterruptedPurchase != null)
            return ReportFailure("An unresolved purchase cannot be reset: reconcile it on the bound character first.");
        pendingReload = false;
        state.CompletedItems.Clear();
        failures.Clear();
        // Preserve the character binding while the task remains selected.
        if (!IsSelected)
            state.CharacterId = 0;
        save();
        Report("Progress reset. Run remaining now or reload to purchase another set.");
        return true;
    }

    public bool RunRemainingNow()
    {
        pendingReload = false;
        if (!IsSelected)
            return ReportFailure("Arm Attempt remaining items after reload on this character first.");
        if (IsRunning)
            return false;
        if (!IsBoundCharacter || !runtime.IsReady)
            return ReportFailure("The bound character must be logged in and ready.");
        try { return BeginPass(); }
        catch (Exception ex)
        {
            Stop($"Relic test stopped safely: {ex.Message}");
            return false;
        }
    }

    public void Stop(string reason = "Stopped this pass; the saved reload selection is retained.")
    {
        pendingReload = false;
        var wasRunning = IsRunning;
        IsRunning = false;
        cleaning = false;
        activeItem = 0;
        if (wasRunning)
            runtime.Cancel();
        Report(reason);
    }

    public void Update()
    {
        try
        {
            if (pendingReload && runtime.IsReady)
            {
                pendingReload = false; // Consume before cleanup or any availability check.
                if (!IsSelected || runtime.CharacterId != loadedCharacterId || !IsBoundCharacter)
                {
                    Report("Reload attempt blocked: this is not the character that armed the test.");
                    return;
                }
                BeginPass();
            }
            if (!IsRunning)
                return;
            if (!IsSelected || !IsBoundCharacter)
            {
                Stop("Pass cancelled: selection or character changed.");
                return;
            }

            if (cleaning)
            {
                runtime.ContinueCleanup();
                var blocker = runtime.CleanupBlocker;
                if (blocker != null || !runtime.IsReady)
                {
                    if (clock.UtcNow - cleanupStarted >= TimeSpan.FromSeconds(10))
                        Stop($"Cleanup blocked: {blocker ?? "character is not ready"}.");
                    return;
                }
                cleaning = false;
                if (!ReconcileInterruptedPurchase())
                    return;
                if (runtime.UnavailableReason is { } reason)
                {
                    Stop($"Pass unavailable: {reason}");
                    return;
                }
            }

            if (activeItem != 0)
            {
                var purchase = runtime.PurchaseStatus;
                if (purchase.Running)
                    return;
                if (purchase.ItemId != activeItem)
                {
                    Stop("Purchase ownership changed; the remaining pass was blocked.");
                    return;
                }
                if (!state.CompletedItems.Contains(activeItem))
                {
                    failures[activeItem] = string.IsNullOrEmpty(purchase.FailureMessage)
                        ? "Purchase ended without a verified completion." : purchase.FailureMessage;
                    diagnostic($"Item {activeItem}: {failures[activeItem]}");
                    if (state.InterruptedPurchase != null || runtime.HasPurchaseSubmission)
                    {
                        Stop("Purchase outcome is uncertain; reconcile before retrying.");
                        return;
                    }
                }
                activeItem = 0;
                BeginCleanup();
                return;
            }

            if (!runtime.IsReady || runtime.UnavailableReason is { })
            {
                Stop($"Pass unavailable: {runtime.UnavailableReason ?? "character is not ready"}.");
                return;
            }
            var next = RelicPurchaseTestCatalog.Items.FirstOrDefault(item => !state.CompletedItems.Contains(item.ItemId) && !attempted.Contains(item.ItemId));
            if (next == null)
            {
                IsRunning = false;
                Report(RelicPurchaseTestCatalog.Items.All(item => state.CompletedItems.Contains(item.ItemId))
                    ? "Complete: all 13 additional relic materials were verified. Later reloads are no-ops."
                    : "Pass finished. Remaining items have visible blockers; retry with Run remaining now or another reload.");
                return;
            }
            attempted.Add(next.ItemId);
            activeItem = next.ItemId;
            Report($"Attempting one additional {next.Name}.");
            if (!runtime.Start(next.ItemId, BeforeSubmit, Verified))
            {
                // A rejected start still exposes the previous runner's status. Never use its result.
                failures[next.ItemId] = runtime.PurchaseStatus.LastStartError;
                diagnostic($"Item {next.ItemId} rejected: {failures[next.ItemId]}");
                activeItem = 0;
                BeginCleanup();
            }
        }
        catch (Exception ex)
        {
            Stop($"Relic test stopped safely: {ex.Message}");
        }
    }

    private bool BeginPass()
    {
        if (state.InterruptedPurchase == null && RelicPurchaseTestCatalog.Items.All(item => state.CompletedItems.Contains(item.ItemId)))
        {
            Report("Already complete. Reset progress explicitly to buy another set.");
            return true;
        }
        attempted.Clear();
        failures.Clear();
        IsRunning = true;
        activeItem = 0;
        BeginCleanup();
        return true;
    }

    private void BeginCleanup()
    {
        cleaning = true;
        cleanupStarted = clock.UtcNow;
        runtime.BeginCleanup();
        Report("Cleaning up before checking the remaining purchases.");
    }

    private void BeforeSubmit(ShopPurchaseCheckpoint checkpoint)
    {
        if (!IsRunning || !IsSelected || !IsBoundCharacter || !runtime.IsReady || checkpoint.ItemId != activeItem
            || checkpoint.Quantity != 1 || checkpoint.Currency != Currency || checkpoint.CurrencyCost <= 0
            || state.InterruptedPurchase != null || state.CompletedItems.Contains(activeItem))
            throw new InvalidOperationException("Relic purchase checkpoint does not match the active bound test.");
        state.InterruptedPurchase = new(checkpoint.ItemId, checkpoint.ItemCountBefore, checkpoint.CurrencyBefore, checkpoint.CurrencyCost);
        save(); // Must finish before the runner can send a purchase callback.
    }

    private void Verified(ShopPurchaseCheckpoint checkpoint)
    {
        var pending = state.InterruptedPurchase;
        if (!IsBoundCharacter || checkpoint.Quantity != 1 || checkpoint.Currency != Currency || pending == null
            || pending != new RelicPurchaseInterruption(checkpoint.ItemId, checkpoint.ItemCountBefore, checkpoint.CurrencyBefore, checkpoint.CurrencyCost))
            throw new InvalidOperationException("Verified purchase does not match its saved checkpoint.");
        CompletePending(pending);
    }

    private bool ReconcileInterruptedPurchase()
    {
        if (state.InterruptedPurchase is not { } pending)
            return true;
        if (pending.Cost > 0 && pending.PoeticsBefore >= pending.Cost && pending.ItemCountBefore >= 0
            && RelicPurchaseTestCatalog.Items.Any(item => item.ItemId == pending.ItemId)
            && runtime.ItemCount(pending.ItemId) - pending.ItemCountBefore == 1
            && pending.PoeticsBefore - runtime.Poetics == pending.Cost)
        {
            CompletePending(pending);
            return true;
        }
        Stop("Interrupted purchase is uncertain: exact item and Poetics changes could not be verified. Retry and reset remain blocked.");
        return false;
    }

    private void CompletePending(RelicPurchaseInterruption pending)
    {
        state.CompletedItems.Add(pending.ItemId);
        state.InterruptedPurchase = null;
        try { save(); }
        catch
        {
            state.CompletedItems.Remove(pending.ItemId);
            state.InterruptedPurchase = pending;
            throw;
        }
        failures.Remove(pending.ItemId);
        diagnostic($"Item {pending.ItemId} verified: +1 item, -{pending.Cost} Poetics. Progress saved.");
    }

    private bool ReportFailure(string message) { Report(message); return false; }
    private void Report(string message) { Status = message; diagnostic(message); }
}
