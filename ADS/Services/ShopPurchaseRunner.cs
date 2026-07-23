using ADS.Models;

namespace ADS.Services;

internal interface IShopPurchaseClock
{
    DateTime UtcNow { get; }
}

internal sealed class SystemShopPurchaseClock : IShopPurchaseClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

internal sealed class ShopPurchaseRunner
{
    private enum RunnerPhase
    {
        Idle,
        Resolving,
        Teleporting,
        Navigating,
        Interacting,
        OpeningMenu,
        ValidatingUi,
        Purchasing,
        VerifyingInventory,
        Completed,
        Failed,
        Cancelled,
    }

    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FloorResolutionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ShopOpeningTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeltaTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaximumAttempts = 3;
    private const int MaximumTransactionsPerCallback = 99;
    private const float InteractionDistance = 3.5f;
    private const float LiveNpcRetargetDistance = 1f;

    private readonly IShopCatalog catalog;
    private readonly IShopPurchaseRuntime runtime;
    private readonly IShopPurchaseClock clock;
    private readonly Func<bool> isDutyOwned;
    private readonly Func<bool> isInnEntryRunning;
    private readonly Action<string> diagnostic;

    private RunnerPhase phase;
    private ShopPurchaseRequest request;
    private ShopCatalogResolution? resolution;
    private ShopOfferSelectionResult? selection;
    private EvaluatedShopOffer? selected;
    private IReadOnlyList<EvaluatedShopOffer> fallbacks = [];
    private int fallbackIndex;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime phaseStartedAtUtc = DateTime.MinValue;
    private DateTime lastActionAtUtc = DateTime.MinValue;
    private int phaseAttempts;
    private int menuPathIndex;
    private long initialItemCount;
    private long lastVerifiedItemCount;
    private bool anyPurchaseCallbackSent;
    private bool teleportCommandAccepted;
    private bool navigationOwned;
    private System.Numerics.Vector3? navigationDestination;
    private bool navigationUsingLiveNpc;
    private bool shopUiOwned;
    private bool interactionSent;
    private int callbackTransactions;
    private long callbackItemCountBefore;
    private IReadOnlyDictionary<uint, long> callbackOutputsBefore = new Dictionary<uint, long>();
    private IReadOnlyDictionary<ShopCurrencyIdentity, long> callbackCurrenciesBefore =
        new Dictionary<ShopCurrencyIdentity, long>();
    private IReadOnlyDictionary<ShopCurrencyIdentity, long> lastVerifiedCurrencies =
        new Dictionary<ShopCurrencyIdentity, long>();
    private IReadOnlyDictionary<uint, long> lastVerifiedOutputs = new Dictionary<uint, long>();
    private readonly List<string> candidateFailures = [];
    private bool sawUiCandidateFailure;
    private string lastValidationDiagnostic = string.Empty;
    private string lastStartError = string.Empty;
    private ShopPurchaseStatusSnapshot status = EmptyStatus();

    public ShopPurchaseRunner(
        IShopCatalog catalog,
        IShopPurchaseRuntime runtime,
        IShopPurchaseClock clock,
        Func<bool>? isDutyOwned = null,
        Func<bool>? isInnEntryRunning = null,
        Action<string>? diagnostic = null)
    {
        this.catalog = catalog;
        this.runtime = runtime;
        this.clock = clock;
        this.isDutyOwned = isDutyOwned ?? (() => false);
        this.isInnEntryRunning = isInnEntryRunning ?? (() => false);
        this.diagnostic = diagnostic ?? (_ => { });
    }

    public bool IsRunning => status.Running;
    public ShopPurchaseStatusSnapshot Status => status with { LastStartError = lastStartError };

    public bool Start(ShopPurchaseRequest purchaseRequest)
    {
        if (!ShopPurchaseRequest.TryCreate(purchaseRequest.ItemId, purchaseRequest.Quantity, out purchaseRequest, out var validationError))
            return RejectStart(validationError);
        if (IsRunning)
            return RejectStart("Cannot start a shop purchase while another shop purchase is active.");
        if (isDutyOwned() || isInnEntryRunning())
            return RejectStart("Cannot start a shop purchase while ADS owns a duty or inn entry is active.");
        if (!runtime.IsPlayerAvailable)
            return RejectStart("Shop purchasing requires a logged-in, available character who is not zoning.");
        if (!runtime.HasVnavmesh)
            return RejectStart("Shop purchasing requires the vnavmesh plugin.");
        if (runtime.HasUnexpectedConfirmation || runtime.IsAnyShopVisible || runtime.IsSelectionMenuVisible)
            return RejectStart("Close existing shop, selection, and confirmation UI before starting shop purchasing.");

        ShopCatalogResolution nextResolution;
        ShopOfferSelectionResult nextSelection;
        try
        {
            nextResolution = catalog.Resolve(purchaseRequest.ItemId, purchaseRequest.Quantity);
            nextSelection = ShopOfferSelector.Select(nextResolution, BuildSelectionContext());
        }
        catch (Exception ex)
        {
            return RejectStart($"Shop catalog resolution failed: {ex.Message}");
        }

        if (nextSelection.Selected?.Route?.RequiresTeleport == true && !runtime.HasLifestream)
            return RejectStart("The selected shop route requires the Lifestream plugin.");

        request = purchaseRequest;
        resolution = nextResolution;
        selection = nextSelection;
        selected = nextSelection.Selected;
        fallbacks = nextSelection.IdenticalCostFallbacks;
        fallbackIndex = 0;
        initialItemCount = runtime.GetItemCount(request.ItemId);
        lastVerifiedItemCount = initialItemCount;
        anyPurchaseCallbackSent = false;
        teleportCommandAccepted = false;
        navigationOwned = false;
        navigationDestination = null;
        navigationUsingLiveNpc = false;
        shopUiOwned = false;
        interactionSent = false;
        callbackTransactions = 0;
        callbackOutputsBefore = new Dictionary<uint, long>();
        callbackCurrenciesBefore = new Dictionary<ShopCurrencyIdentity, long>();
        lastVerifiedCurrencies = selected?.Offer.Currencies
            .ToDictionary(currency => currency.Identity, runtime.GetAvailableCurrency)
            ?? new Dictionary<ShopCurrencyIdentity, long>();
        lastVerifiedOutputs = selected?.Offer.AllOutputs
            .ToDictionary(output => output.ItemId, output => runtime.GetItemCount(output.ItemId))
            ?? new Dictionary<uint, long>();
        candidateFailures.Clear();
        sawUiCandidateFailure = false;
        lastValidationDiagnostic = string.Empty;
        startedAtUtc = clock.UtcNow;
        lastStartError = string.Empty;
        SetPhase(RunnerPhase.Resolving, $"Resolving supported shops for {nextResolution.ItemName}.");
        status = new ShopPurchaseStatusSnapshot(
            true,
            false,
            null,
            PhaseName(phase),
            request.ItemId,
            nextResolution.ItemName,
            request.Quantity,
            0,
            request.Quantity,
            selected == null ? null : ShopOfferSelector.ToStatus(selected),
            AlternativeStatuses(selected, nextSelection.Alternatives),
            null,
            $"Resolving supported shops for {nextResolution.ItemName}.",
            string.Empty,
            string.Empty,
            lastStartError,
            null);
        ReportSelectionDiagnostics(nextSelection);
        return true;
    }

    public bool RejectStart(string message)
    {
        lastStartError = string.IsNullOrWhiteSpace(message) ? "Shop purchase was rejected." : message.Trim();
        status = status with { LastStartError = lastStartError };
        return false;
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (!runtime.IsLoggedIn)
            {
                Cancel("The character logged out during the shop purchase.");
                return;
            }

            if (isDutyOwned() || isInnEntryRunning())
            {
                Cancel("ADS ownership changed during the shop purchase.");
                return;
            }

            if (clock.UtcNow - startedAtUtc > CompleteTimeout)
            {
                Fail(ShopPurchaseFailureCodes.Timeout, "Shop purchase exceeded the five-minute run limit.");
                return;
            }

            if (runtime.IsBetweenAreas)
            {
                if (phase != RunnerPhase.Teleporting)
                    Fail(ShopPurchaseFailureCodes.NoRoute, "Unexpected zoning interrupted the shop purchase.");
                else if (clock.UtcNow - phaseStartedAtUtc > TravelTimeout)
                    Fail(ShopPurchaseFailureCodes.Timeout, "Travel remained in a zoning transition for more than 90 seconds.");
                return;
            }

            if (!runtime.IsPlayerAvailable)
            {
                Cancel("The player became unavailable during the shop purchase.");
                return;
            }

            if (runtime.HasUnexpectedConfirmation)
            {
                var ownedConfirmationPending = phase == RunnerPhase.VerifyingInventory
                    && selected != null
                    && callbackTransactions > 0
                    && runtime.IsOwnedConfirmationPending(selected, callbackTransactions);
                if (!ownedConfirmationPending)
                {
                    if (phase == RunnerPhase.VerifyingInventory
                        && selected != null
                        && callbackTransactions > 0
                        && runtime.TryAcceptOwnedConfirmation(selected, callbackTransactions))
                    {
                        return;
                    }
                    StopOwnedNavigation();
                    Fail(ShopPurchaseFailureCodes.UiMismatch, "An unexpected confirmation dialog appeared; ADS did not accept it.");
                    return;
                }
            }

            if (phase != RunnerPhase.VerifyingInventory
                && phase != RunnerPhase.Teleporting
                && TryGetExternalBalanceChange(out var balanceChange))
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, balanceChange);
                return;
            }

            switch (phase)
            {
                case RunnerPhase.Resolving:
                    UpdateResolving();
                    break;
                case RunnerPhase.Teleporting:
                    UpdateTeleporting();
                    break;
                case RunnerPhase.Navigating:
                    UpdateNavigating();
                    break;
                case RunnerPhase.Interacting:
                    UpdateInteracting();
                    break;
                case RunnerPhase.OpeningMenu:
                    UpdateOpeningMenu();
                    break;
                case RunnerPhase.ValidatingUi:
                    UpdateValidatingUi();
                    break;
                case RunnerPhase.Purchasing:
                    UpdatePurchasing();
                    break;
                case RunnerPhase.VerifyingInventory:
                    UpdateVerifyingInventory();
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, $"Shop purchase failed safely: {ex.Message}");
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning)
            return;
        Finish(false, RunnerPhase.Cancelled, ShopPurchaseFailureCodes.Cancelled, $"Shop purchase cancelled: {reason}");
    }

    private void UpdateResolving()
    {
        if (selection?.Selected == null || selected?.Route == null)
        {
            Fail(
                selection?.FailureCode ?? ShopPurchaseFailureCodes.UnsupportedOffer,
                selection?.Message ?? "No supported shop offer was selected.");
            return;
        }

        if (selected.Route.RequiresTeleport)
        {
            if (!runtime.HasLifestream)
            {
                Fail(ShopPurchaseFailureCodes.MissingDependency, "The selected route requires Lifestream.");
                return;
            }

            BeginTeleport();
            return;
        }

        BeginNavigation();
    }

    private void BeginTeleport()
    {
        teleportCommandAccepted = false;
        SetPhase(RunnerPhase.Teleporting, $"Teleporting to {selected!.Route!.AetheryteName} for {selected.Offer.NpcName}.");
        TryTeleportNow();
    }

    private void UpdateTeleporting()
    {
        if (selected?.Route == null)
        {
            Fail(ShopPurchaseFailureCodes.NoRoute, "The selected teleport route was lost.");
            return;
        }

        if (runtime.CurrentTerritoryId == selected.Route.TerritoryId)
        {
            RebaselineTrackedGilAfterTeleport();
            if (TryGetExternalBalanceChange(out var balanceChange))
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, balanceChange);
                return;
            }
            BeginNavigation();
            return;
        }

        if (clock.UtcNow - phaseStartedAtUtc > TravelTimeout)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.Timeout, "Travel to the selected shop timed out.");
            return;
        }

        if (teleportCommandAccepted)
            return;

        if (phaseAttempts >= MaximumAttempts && clock.UtcNow - lastActionAtUtc >= ActionRetryDelay)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.NoRoute, "Lifestream did not accept the selected shop route.");
            return;
        }

        if (clock.UtcNow - lastActionAtUtc >= ActionRetryDelay)
            TryTeleportNow();
    }

    private void TryTeleportNow()
    {
        if (selected?.Route == null || phaseAttempts >= MaximumAttempts)
            return;
        phaseAttempts++;
        lastActionAtUtc = clock.UtcNow;
        teleportCommandAccepted = runtime.TryTeleport(selected.Route);
        if (teleportCommandAccepted)
        {
            SetStatus($"Teleport command accepted; waiting up to 90 seconds to enter {selected.Route.TerritoryName}.");
            diagnostic($"Teleport command accepted on send {phaseAttempts}; zoning monitoring is active without resends.");
        }
        else
        {
            SetStatus($"Lifestream teleport command send {phaseAttempts} of {MaximumAttempts} failed; waiting to retry.");
            diagnostic($"Teleport command send {phaseAttempts} of {MaximumAttempts} failed.");
        }
    }

    private void BeginNavigation()
    {
        navigationDestination = null;
        navigationUsingLiveNpc = false;
        SetPhase(RunnerPhase.Navigating, $"Navigating to {selected!.Offer.NpcName}.");
        TryPrepareNavigationDestination();
    }

    private void UpdateNavigating()
    {
        if (selected?.Route == null)
        {
            Fail(ShopPurchaseFailureCodes.NoRoute, "The selected NPC route was lost.");
            return;
        }

        if (runtime.CurrentTerritoryId != selected.Route.TerritoryId)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.NoRoute, "The player is not in the selected shop territory.");
            return;
        }

        if (runtime.HasUnexpectedConfirmation)
        {
            StopOwnedNavigation();
            Fail(ShopPurchaseFailureCodes.UiMismatch, "An unexpected confirmation dialog appeared during navigation; ADS did not accept it.");
            return;
        }

        if (runtime.IsAnyShopVisible || runtime.IsSelectionMenuVisible)
        {
            StopOwnedNavigation();
            Fail(ShopPurchaseFailureCodes.UiMismatch, "A shop or selection window appeared unexpectedly during navigation; ADS stopped before interaction.");
            return;
        }

        var hasNpc = runtime.TryGetNpc(selected.Offer.NpcId, out var npc);
        if (hasNpc && navigationDestination == null)
        {
            navigationDestination = npc.Position;
            navigationUsingLiveNpc = true;
        }

        if (!hasNpc && navigationDestination == null && selected.Route.RequiresFloorResolution)
        {
            if (clock.UtcNow - phaseStartedAtUtc > FloorResolutionTimeout)
            {
                TryFallbackOrFail(
                    ShopPurchaseFailureCodes.NoRoute,
                    "vnavmesh could not resolve a floor for the offline NPC placement after entering its territory.");
                return;
            }

            if (clock.UtcNow - lastActionAtUtc >= ActionRetryDelay)
                TryResolveCatalogFloorNow();
            return;
        }

        var destination = hasNpc
            ? npc.Position
            : navigationDestination ?? selected.Route.NpcPosition;
        var distance = hasNpc ? npc.Distance : System.Numerics.Vector3.Distance(runtime.PlayerPosition, destination);
        if (distance <= InteractionDistance)
        {
            StopOwnedNavigation();
            interactionSent = false;
            SetPhase(RunnerPhase.Interacting, $"Interacting with {selected.Offer.NpcName}.");
            return;
        }

        if (hasNpc && !navigationUsingLiveNpc)
        {
            var horizontalDifference = System.Numerics.Vector2.Distance(
                new System.Numerics.Vector2(destination.X, destination.Z),
                new System.Numerics.Vector2(navigationDestination!.Value.X, navigationDestination.Value.Z));
            if (horizontalDifference > LiveNpcRetargetDistance)
            {
                StopOwnedNavigation();
                navigationDestination = npc.Position;
                navigationUsingLiveNpc = true;
                phaseAttempts = 0;
                lastActionAtUtc = DateTime.MinValue;
                diagnostic(
                    $"Live NPC position replaced the approximate catalog destination; retargeting by {horizontalDifference:F2} world units.");
                TryMoveNow(npc.Position);
                return;
            }

            navigationUsingLiveNpc = true;
            navigationDestination = npc.Position;
        }

        if (clock.UtcNow - phaseStartedAtUtc > NavigationTimeout)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.Timeout, "Navigation to the selected shop NPC timed out.");
            return;
        }

        if (navigationOwned)
            return;

        if (phaseAttempts >= MaximumAttempts && clock.UtcNow - lastActionAtUtc >= ActionRetryDelay)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.NoRoute, "vnavmesh rejected three movement command sends for the selected shop NPC.");
            return;
        }

        if (clock.UtcNow - lastActionAtUtc >= ActionRetryDelay)
            TryMoveNow(destination);
    }

    private void TryPrepareNavigationDestination()
    {
        if (selected?.Route == null)
            return;
        if (runtime.TryGetNpc(selected.Offer.NpcId, out var npc))
        {
            navigationDestination = npc.Position;
            navigationUsingLiveNpc = true;
            TryMoveNow(npc.Position);
            return;
        }
        if (!selected.Route.RequiresFloorResolution)
        {
            navigationDestination = selected.Route.NpcPosition;
            TryMoveNow(navigationDestination);
            return;
        }
        TryResolveCatalogFloorNow();
    }

    private void TryResolveCatalogFloorNow()
    {
        if (selected?.Route == null || navigationDestination != null)
            return;
        phaseAttempts++;
        lastActionAtUtc = clock.UtcNow;
        if (!runtime.TryResolveFloor(selected.Route.NpcPosition, out var floorPosition))
        {
            SetStatus(
                $"Waiting for vnavmesh to resolve the offline placement floor for {selected.Offer.NpcName} after entering {selected.Route.TerritoryName}.");
            diagnostic($"Offline placement floor query attempt {phaseAttempts} returned no point.");
            return;
        }

        navigationDestination = floorPosition;
        navigationUsingLiveNpc = false;
        phaseAttempts = 0;
        lastActionAtUtc = DateTime.MinValue;
        diagnostic(
            $"Offline placement floor resolved at {floorPosition.X:F2},{floorPosition.Y:F2},{floorPosition.Z:F2}.");
        TryMoveNow(floorPosition);
    }

    private void TryMoveNow(System.Numerics.Vector3? destination = null)
    {
        if (selected?.Route == null || phaseAttempts >= MaximumAttempts)
            return;
        var target = destination ?? navigationDestination ?? selected.Route.NpcPosition;
        navigationDestination = target;
        phaseAttempts++;
        lastActionAtUtc = clock.UtcNow;
        var accepted = runtime.TryMove(target, selected.Offer.NpcName);
        if (accepted)
        {
            navigationOwned = true;
            SetStatus($"Navigation command accepted; monitoring distance to {selected.Offer.NpcName} for up to 120 seconds.");
            diagnostic($"Navigation command accepted on send {phaseAttempts}; distance monitoring is active without resends.");
        }
        else
        {
            SetStatus($"vnavmesh movement command send {phaseAttempts} of {MaximumAttempts} failed; waiting to retry.");
            diagnostic($"Navigation command send {phaseAttempts} of {MaximumAttempts} failed.");
        }
    }

    private void UpdateInteracting()
    {
        if (selected == null)
        {
            Fail(ShopPurchaseFailureCodes.NoRoute, "The selected shop NPC was lost.");
            return;
        }

        if (runtime.HasUnexpectedConfirmation)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "An unexpected confirmation dialog appeared; ADS did not accept it.");
            return;
        }

        if (runtime.IsExpectedShopVisible(selected.Offer.Kind) || runtime.IsSelectionMenuVisible)
        {
            if (!interactionSent)
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, "A shop or selection menu was already open before ADS interacted with the resolved NPC.");
                return;
            }

            SetPhase(RunnerPhase.OpeningMenu, $"Opening {selected.Offer.ShopName}.");
            return;
        }

        if (runtime.TryGetNpc(selected.Offer.NpcId, out var npc) && npc.Distance > InteractionDistance)
        {
            BeginNavigation();
            return;
        }

        if (clock.UtcNow - phaseStartedAtUtc > ShopOpeningTimeout
            || (phaseAttempts >= MaximumAttempts && clock.UtcNow - lastActionAtUtc >= ActionRetryDelay))
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.NoRoute, "The selected shop NPC did not open a supported shop menu.");
            return;
        }

        if (clock.UtcNow - lastActionAtUtc < ActionRetryDelay)
            return;
        phaseAttempts++;
        lastActionAtUtc = clock.UtcNow;
        if (runtime.TryInteractNpc(selected.Offer.NpcId))
        {
            interactionSent = true;
            shopUiOwned = true;
        }
    }

    private void UpdateOpeningMenu()
    {
        if (selected == null)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "The selected shop was lost while opening its menu.");
            return;
        }

        if (runtime.HasUnexpectedConfirmation)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "An unexpected confirmation dialog appeared; ADS did not accept it.");
            return;
        }

        if (runtime.IsExpectedShopVisible(selected.Offer.Kind))
        {
            SetPhase(RunnerPhase.ValidatingUi, "Validating the live shop row against sheet data.");
            return;
        }

        if (clock.UtcNow - phaseStartedAtUtc > ShopOpeningTimeout)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.Timeout, "The supported shop addon did not open in time.");
            return;
        }

        if (!runtime.IsSelectionMenuVisible
            || menuPathIndex >= selected.Offer.MenuPath.Count
            || clock.UtcNow - lastActionAtUtc < ActionRetryDelay)
        {
            return;
        }

        if (phaseAttempts >= MaximumAttempts)
        {
            TryFallbackOrFail(ShopPurchaseFailureCodes.NoRoute, "The selected shop menu did not accept a required menu entry.");
            return;
        }

        var step = selected.Offer.CallbackPath[menuPathIndex];
        phaseAttempts++;
        lastActionAtUtc = clock.UtcNow;
        if (!runtime.TrySelectMenu(step, selected.Offer.NpcId))
            return;
        menuPathIndex++;
        phaseAttempts = 0;
        SetStatus($"Selected shop menu entry {menuPathIndex} of {selected.Offer.MenuPath.Count}.");
    }

    private void UpdateValidatingUi()
    {
        if (selected == null)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "The selected offer was lost during UI validation.");
            return;
        }

        var validation = runtime.ValidateShopUi(selected);
        ReportValidation(validation);
        switch (validation.State)
        {
            case ShopUiValidationState.Valid:
                SetPhase(RunnerPhase.Purchasing, "Live shop row validated; preparing a bounded purchase batch.");
                break;
            case ShopUiValidationState.Mismatch:
                TryFallbackOrFail(ShopPurchaseFailureCodes.UiMismatch, validation.Message, isUiCandidateFailure: true);
                break;
            default:
                if (clock.UtcNow - phaseStartedAtUtc > ShopOpeningTimeout)
                {
                    TryFallbackOrFail(
                        ShopPurchaseFailureCodes.UiMismatch,
                        $"Timed out validating live shop UI: {validation.Message}",
                        isUiCandidateFailure: true);
                }
                else
                    SetStatus(validation.Message);
                break;
        }
    }

    private void UpdatePurchasing()
    {
        if (selected == null)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "The selected offer was lost before purchase.");
            return;
        }

        RefreshAcquiredTruth();
        if (runtime.GetItemCount(request.ItemId) != lastVerifiedItemCount)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "Requested-item inventory changed outside a verified purchase callback.");
            return;
        }

        if (status.RemainingQuantity == 0)
        {
            Complete();
            return;
        }

        if (status.RemainingQuantity % selected.Offer.ReceiveCount != 0)
        {
            Fail(ShopPurchaseFailureCodes.UnsupportedOffer, "The remaining quantity is not divisible by the selected shop bundle.");
            return;
        }

        var remainingTransactions = status.RemainingQuantity / (int)selected.Offer.ReceiveCount;
        if (selected.Offer.AllOutputs.Any(output =>
                runtime.GetInventoryCapacity(output.ItemId, Math.Max(1, output.StackSize))
                    < checked((long)output.Count * remainingTransactions)))
        {
            Fail(ShopPurchaseFailureCodes.InventoryCapacity, "Inventory no longer has capacity for every output in the remaining exchange.");
            return;
        }

        var validation = runtime.ValidateShopUi(selected);
        ReportValidation(validation);
        if (validation.State != ShopUiValidationState.Valid)
        {
            TryFallbackOrFail(
                ShopPurchaseFailureCodes.UiMismatch,
                validation.Message,
                isUiCandidateFailure: true);
            return;
        }

        callbackTransactions = Math.Min(
            MaximumTransactionsPerCallback,
            status.RemainingQuantity / (int)selected.Offer.ReceiveCount);
        if (callbackTransactions <= 0)
        {
            Fail(ShopPurchaseFailureCodes.UnsupportedOffer, "No exact purchase transaction could be formed for the remaining quantity.");
            return;
        }

        callbackItemCountBefore = lastVerifiedItemCount;
        callbackOutputsBefore = selected.Offer.AllOutputs
            .ToDictionary(output => output.ItemId, output => runtime.GetItemCount(output.ItemId));
        var before = new Dictionary<ShopCurrencyIdentity, long>();
        foreach (var currency in selected.Offer.Currencies)
        {
            var available = runtime.GetAvailableCurrency(currency);
            var required = checked((long)currency.AmountPerTransaction * callbackTransactions);
            if (available < required)
            {
                Fail(ShopPurchaseFailureCodes.InsufficientCurrency, $"Insufficient {currency.Name} for the next verified purchase batch.");
                return;
            }

            before[currency.Identity] = available;
        }

        callbackCurrenciesBefore = before;
        if (!runtime.SubmitPurchase(selected, validation.RuntimeRow, callbackTransactions))
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "The validated shop callback was not accepted; ADS did not retry it.");
            return;
        }

        anyPurchaseCallbackSent = true;
        SetPhase(
            RunnerPhase.VerifyingInventory,
            $"Verifying inventory and currency deltas for {callbackTransactions} shop transaction(s).");
    }

    private void UpdateVerifyingInventory()
    {
        if (selected == null || callbackTransactions <= 0)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "Purchase verification state was incomplete.");
            return;
        }

        var currentItemCount = runtime.GetItemCount(request.ItemId);
        var expectedItemDelta = checked((long)selected.Offer.ReceiveCount * callbackTransactions);
        var itemDelta = currentItemCount - callbackItemCountBefore;
        RefreshAcquiredTruth(currentItemCount);
        if (itemDelta < 0 || itemDelta > expectedItemDelta)
        {
            Fail(ShopPurchaseFailureCodes.UiMismatch, "Requested-item inventory changed by an amount that contradicts the submitted shop batch.");
            return;
        }

        var outputsComplete = true;
        foreach (var output in selected.Offer.AllOutputs)
        {
            if (!callbackOutputsBefore.TryGetValue(output.ItemId, out var outputBefore))
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, "Output verification state was incomplete.");
                return;
            }
            var current = runtime.GetItemCount(output.ItemId);
            var expectedDelta = checked((long)output.Count * callbackTransactions);
            var actualDelta = current - outputBefore;
            if (actualDelta < 0 || actualDelta > expectedDelta)
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, $"{output.Name} changed by an amount that contradicts the submitted shop batch.");
                return;
            }
            outputsComplete &= actualDelta == expectedDelta;
        }

        var currenciesComplete = true;
        foreach (var currency in selected.Offer.Currencies)
        {
            if (!callbackCurrenciesBefore.TryGetValue(currency.Identity, out var currencyBefore))
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, "Currency verification state was incomplete.");
                return;
            }

            var current = runtime.GetAvailableCurrency(currency);
            var expectedDelta = checked((long)currency.AmountPerTransaction * callbackTransactions);
            var actualDelta = currencyBefore - current;
            if (actualDelta < 0 || actualDelta > expectedDelta)
            {
                Fail(ShopPurchaseFailureCodes.UiMismatch, $"{currency.Name} changed by an amount that contradicts the submitted shop batch.");
                return;
            }

            currenciesComplete &= actualDelta == expectedDelta;
        }

        if (itemDelta == expectedItemDelta && outputsComplete && currenciesComplete)
        {
            lastVerifiedItemCount = currentItemCount;
            lastVerifiedCurrencies = selected.Offer.Currencies
                .ToDictionary(currency => currency.Identity, runtime.GetAvailableCurrency);
            lastVerifiedOutputs = selected.Offer.AllOutputs
                .ToDictionary(output => output.ItemId, output => runtime.GetItemCount(output.ItemId));
            RefreshAcquiredTruth(currentItemCount);
            callbackTransactions = 0;
            callbackCurrenciesBefore = new Dictionary<ShopCurrencyIdentity, long>();
            callbackOutputsBefore = new Dictionary<uint, long>();
            if (status.RemainingQuantity == 0)
                Complete();
            else
                SetPhase(RunnerPhase.ValidatingUi, "Purchase batch verified; revalidating the shop before the next batch.");
            return;
        }

        if (clock.UtcNow - phaseStartedAtUtc > DeltaTimeout)
        {
            Fail(ShopPurchaseFailureCodes.Timeout, "Timed out waiting for the exact item and currency deltas; ADS did not resend the callback.");
            return;
        }

        SetStatus("Waiting for the exact item and currency deltas from the submitted shop batch.");
    }

    private void TryFallbackOrFail(string failureCode, string message, bool isUiCandidateFailure = false)
    {
        if (anyPurchaseCallbackSent)
        {
            Fail(failureCode, message);
            return;
        }

        StopOwnedNavigation();
        CloseOwnedShopUi();
        var failedCandidate = selected;
        var failureSummary = failedCandidate == null
            ? message
            : $"shop={failedCandidate.Offer.ShopId}, npc={failedCandidate.Offer.NpcId}, territory={failedCandidate.Offer.TerritoryId}, "
                + $"placement={failedCandidate.Offer.PlacementSource}, path={ShopCatalogBuilder.CallbackPathSignature(failedCandidate.Offer.CallbackPath)}: {message}";
        candidateFailures.Add(failureSummary);
        sawUiCandidateFailure |= isUiCandidateFailure;
        diagnostic($"Candidate {candidateFailures.Count} failed before callback ({failureCode}): {failureSummary}");
        if (fallbackIndex >= fallbacks.Count)
        {
            var finalCode = sawUiCandidateFailure ? ShopPurchaseFailureCodes.UiMismatch : failureCode;
            var finalMessage = candidateFailures.Count <= 1
                ? message
                : $"All {candidateFailures.Count} identical-cost candidates failed before purchase. Last failure: {message}";
            Fail(finalCode, finalMessage);
            return;
        }

        selected = fallbacks[fallbackIndex++];
        teleportCommandAccepted = false;
        navigationDestination = null;
        navigationUsingLiveNpc = false;
        interactionSent = false;
        diagnostic(
            $"Switching to identical-cost fallback {fallbackIndex} of {fallbacks.Count}: shop={selected.Offer.ShopId}, "
            + $"npc={selected.Offer.NpcId}, territory={selected.Offer.TerritoryId}, placement={selected.Offer.PlacementSource}, "
            + $"path={ShopCatalogBuilder.CallbackPathSignature(selected.Offer.CallbackPath)}.");
        status = status with
        {
            SelectedOffer = ShopOfferSelector.ToStatus(selected),
            AlternativeOffers = AlternativeStatuses(selected, selection?.Alternatives ?? []),
        };
        if (selected.Route?.RequiresTeleport == true)
        {
            if (!runtime.HasLifestream)
            {
                Fail(ShopPurchaseFailureCodes.MissingDependency, "The identical-cost fallback route requires Lifestream.");
                return;
            }

            BeginTeleport();
        }
        else
        {
            BeginNavigation();
        }
    }

    private ShopSelectionContext BuildSelectionContext()
        => new(
            runtime.CurrentTerritoryId,
            runtime.PlayerPosition,
            runtime.IsAetheryteUnlocked,
            runtime.IsQuestComplete,
            runtime.GetAvailableCurrency,
            runtime.GetItemCount,
            runtime.GetInventoryCapacity,
            () => runtime.CurrentGrandCompany,
            () => runtime.CurrentGrandCompanyRank);

    private void Complete()
        => Finish(
            true,
            RunnerPhase.Completed,
            null,
            $"Purchased exactly {request.Quantity} additional {resolution?.ItemName ?? $"item {request.ItemId}"}.");

    private void Fail(string failureCode, string message)
        => Finish(false, RunnerPhase.Failed, failureCode, message);

    private void Finish(bool succeeded, RunnerPhase terminalPhase, string? failureCode, string message)
    {
        StopOwnedNavigation();
        RefreshAcquiredTruth();
        CloseOwnedShopUi();
        var previous = phase;
        phase = terminalPhase;
        diagnostic($"Phase {PhaseName(previous)} -> {PhaseName(terminalPhase)}: {message}");
        var completed = clock.UtcNow;
        status = status with
        {
            Running = false,
            Done = true,
            Succeeded = succeeded,
            Phase = PhaseName(phase),
            FailureCode = failureCode,
            StatusMessage = message,
            SuccessMessage = succeeded ? message : string.Empty,
            FailureMessage = succeeded ? string.Empty : message,
            LastStartError = lastStartError,
            CompletedAtUtc = completed,
        };
    }

    private void RefreshAcquiredTruth(long? currentItemCount = null)
    {
        if (request.ItemId == 0)
            return;
        var current = currentItemCount ?? runtime.GetItemCount(request.ItemId);
        var delta = Math.Max(0, current - initialItemCount);
        var acquired = delta > int.MaxValue ? int.MaxValue : (int)delta;
        status = status with
        {
            AcquiredQuantity = acquired,
            RemainingQuantity = Math.Max(0, request.Quantity - acquired),
        };
    }

    private void SetPhase(RunnerPhase next, string message)
    {
        var previous = phase;
        phase = next;
        phaseStartedAtUtc = clock.UtcNow;
        lastActionAtUtc = DateTime.MinValue;
        phaseAttempts = 0;
        lastValidationDiagnostic = string.Empty;
        if (next == RunnerPhase.OpeningMenu)
            menuPathIndex = 0;
        SetStatus(message);
        diagnostic($"Phase {PhaseName(previous)} -> {PhaseName(next)}: {message}");
    }

    private void SetStatus(string message)
    {
        status = status with
        {
            Phase = PhaseName(phase),
            StatusMessage = message,
            LastStartError = lastStartError,
        };
    }

    private void StopOwnedNavigation()
    {
        if (!navigationOwned)
            return;
        runtime.StopNavigation();
        navigationOwned = false;
    }

    private void ReportValidation(ShopUiValidationResult validation)
    {
        var current = $"{validation.State}:{validation.RuntimeRow}:{validation.Message}";
        if (string.Equals(current, lastValidationDiagnostic, StringComparison.Ordinal))
            return;
        lastValidationDiagnostic = current;
        diagnostic($"Validation {validation.State} row={validation.RuntimeRow}: {validation.Message}");
    }

    private void CloseOwnedShopUi()
    {
        if (!shopUiOwned)
            return;
        runtime.CloseOwnedShopUi();
        shopUiOwned = false;
    }

    private bool TryGetExternalBalanceChange(out string message)
    {
        message = string.Empty;
        if (selected == null || request.ItemId == 0)
            return false;

        var currentItemCount = runtime.GetItemCount(request.ItemId);
        if (currentItemCount != lastVerifiedItemCount)
        {
            message = "Requested-item inventory changed outside ADS's verified purchase callback window.";
            return true;
        }

        foreach (var output in selected.Offer.AllOutputs)
        {
            if (!lastVerifiedOutputs.TryGetValue(output.ItemId, out var expected))
            {
                message = $"Tracked {output.Name} state was incomplete before purchase.";
                return true;
            }
            if (runtime.GetItemCount(output.ItemId) == expected)
                continue;
            message = $"{output.Name} changed outside ADS's verified purchase callback window.";
            return true;
        }

        foreach (var currency in selected.Offer.Currencies)
        {
            if (!lastVerifiedCurrencies.TryGetValue(currency.Identity, out var expected))
            {
                message = $"Tracked {currency.Name} state was incomplete before purchase.";
                return true;
            }

            if (expected < 0)
                continue;

            if (runtime.GetAvailableCurrency(currency) == expected)
                continue;
            message = $"{currency.Name} changed outside ADS's verified purchase callback window.";
            return true;
        }

        return false;
    }

    private void RebaselineTrackedGilAfterTeleport()
    {
        if (selected == null)
            return;

        var trackedGil = selected.Offer.Currencies
            .Where(currency => currency.Kind == ShopCurrencyKind.Gil)
            .ToArray();
        if (trackedGil.Length == 0)
            return;

        var rebased = new Dictionary<ShopCurrencyIdentity, long>(lastVerifiedCurrencies);
        foreach (var currency in trackedGil)
            rebased[currency.Identity] = runtime.GetAvailableCurrency(currency);
        lastVerifiedCurrencies = rebased;
        diagnostic("Confirmed teleport arrival; re-baselined tracked gil while preserving item, output, and non-gil baselines.");
    }

    private void ReportSelectionDiagnostics(ShopOfferSelectionResult result)
    {
        for (var index = 0; index < result.Alternatives.Count; index++)
        {
            var candidate = result.Alternatives[index];
            var route = candidate.Route == null
                ? "unreachable"
                : candidate.Route.RequiresTeleport
                    ? $"teleport:{candidate.Route.AetheryteId}:{candidate.Route.RouteDistance:F1}"
                    : $"same-territory:{candidate.Route.RouteDistance:F1}";
            diagnostic(
                $"Evaluated candidate {index + 1}/{result.Alternatives.Count}: shop={candidate.Offer.ShopId}, npc={candidate.Offer.NpcId}, "
                + $"territory={candidate.Offer.TerritoryId}, placement={candidate.Offer.PlacementSource}, route={route}, "
                + $"gate={candidate.GateSatisfied}, affordable={candidate.Affordable}, capacity={candidate.CapacitySatisfied}, "
                + $"rejection={candidate.EvaluationFailureCode ?? "none"}.");
        }

        if (result.Selected != null)
        {
            diagnostic(
                $"Ranked identical-cost candidates: selected shop={result.Selected.Offer.ShopId}, npc={result.Selected.Offer.NpcId}; "
                + $"fallbacks={result.IdenticalCostFallbacks.Count}.");
        }
        else
        {
            diagnostic($"No candidate selected: failure={result.FailureCode ?? "none"}, message={result.Message}");
        }
    }

    private static IReadOnlyList<ShopPurchaseOfferStatus> AlternativeStatuses(
        EvaluatedShopOffer? current,
        IReadOnlyList<EvaluatedShopOffer> alternatives)
        => alternatives
            .Where(offer => !ReferenceEquals(offer, current))
            .Select(ShopOfferSelector.ToStatus)
            .ToArray();

    private static ShopPurchaseStatusSnapshot EmptyStatus()
        => new(
            false,
            false,
            null,
            "idle",
            0,
            string.Empty,
            0,
            0,
            0,
            null,
            [],
            null,
            "No shop purchase has been started.",
            string.Empty,
            string.Empty,
            string.Empty,
            null);

    private static string PhaseName(RunnerPhase value)
        => value switch
        {
            RunnerPhase.Idle => "idle",
            RunnerPhase.Resolving => "resolving",
            RunnerPhase.Teleporting => "teleporting",
            RunnerPhase.Navigating => "navigating",
            RunnerPhase.Interacting => "interacting",
            RunnerPhase.OpeningMenu => "opening-menu",
            RunnerPhase.ValidatingUi => "validating-ui",
            RunnerPhase.Purchasing => "purchasing",
            RunnerPhase.VerifyingInventory => "verifying-inventory",
            RunnerPhase.Completed => "completed",
            RunnerPhase.Failed => "failed",
            RunnerPhase.Cancelled => "cancelled",
            _ => "unknown",
        };
}
