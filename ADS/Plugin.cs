using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.DutyState;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.GameHelpers;

namespace ADS;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly TimeSpan TreasureDutyRecoveryTtl = TimeSpan.FromHours(8);
    private static readonly TimeSpan TreasureDutyRecoveryRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FrameworkSlowLogCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HigherLowerRecentSignalWindow = TimeSpan.FromSeconds(20);
    private const double FrameworkSlowLogThresholdMs = 100d;

    private enum RemoteJsonReloadStep
    {
        ObjectRules,
        DialogRules,
        DutyMaturity,
        TreasureRoutes,
    }

    private sealed record FrameworkSlowUpdateContext(
        uint territoryTypeId,
        uint mapId,
        bool betweenAreas,
        bool betweenAreas51,
        bool dialogVisible,
        string dialogRule,
        string dialogStatus,
        int pendingHigherLowerVfxCount,
        int trackedHigherLowerVfxCount);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public string Name
        => PluginInfo.DisplayName;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    public DutyCatalogService DutyCatalogService { get; }
    public DutyContextService DutyContextService { get; }
    public ObjectPriorityRuleService ObjectPriorityRuleService { get; }
    public ObservationMemoryService ObservationMemoryService { get; }
    public DialogYesNoRuleService DialogYesNoRuleService { get; }
    public DungeonFrontierService DungeonFrontierService { get; }
    public ObjectivePlannerService ObjectivePlannerService { get; }
    public ExecutionService ExecutionService { get; }
    public DialogAutomationService DialogAutomationService { get; }
    public AdsIpcService AdsIpcService { get; }
    public BmrReflectionService BmrReflectionService { get; }
    public ReflectionIpcService ReflectionIpcService { get; }
    public MapFlagService MapFlagService { get; }
    public InnEntryService InnEntryService { get; }
    public UtilityAutomationService UtilityAutomationService { get; }
    public LootAutomationService LootAutomationService { get; }
    public TreasureFollowerDutyExitMonitorService TreasureFollowerDutyExitMonitorService { get; }
    public RemoteJsonUpdateService RemoteJsonUpdateService { get; }
    public TreasureDungeonRoleDetector TreasureDungeonRoleDetector { get; }
    public TreasurePortalOpenerRelayService TreasurePortalOpenerRelayService { get; }
    public TreasurePortalOpenerTracker TreasurePortalOpenerTracker { get; }
    public BossModMultiboxFollowService BossModMultiboxFollowService { get; }
    public TreasureFollowerAutoMoveAssistService TreasureFollowerAutoMoveAssistService { get; }
    public TreasureHighLowDiagnosticService TreasureHighLowDiagnosticService { get; }
    public HigherLowerServerEventTraceService HigherLowerServerEventTraceService { get; }
    public HigherLowerVfxTraceService HigherLowerVfxTraceService { get; }
    public HigherLowerCardVfxSolverService HigherLowerCardVfxSolverService { get; }
    public HigherLowerAutomationService HigherLowerAutomationService { get; }
    public DebugStrafeService DebugStrafeService { get; }

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly ObjectExplorerWindow objectExplorerWindow;
    private readonly GhostListWindow ghostListWindow;
    private readonly FrontierLabelWindow frontierLabelWindow;
    private readonly QuickControlWindow quickControlWindow;
    private readonly LootWindow lootWindow;
    private readonly ObjectRuleEditorWindow objectRuleEditorWindow;
    private readonly DialogRuleEditorWindow dialogRuleEditorWindow;
    private readonly HigherLowerWindow higherLowerWindow;
    private readonly TreasureRouteEditorWindow treasureRouteEditorWindow;
    private readonly ServerEventExplorerWindow serverEventExplorerWindow;
    private readonly VfxExplorerWindow vfxExplorerWindow;
    private readonly ReflectionWindow reflectionWindow;
    private IDtrBarEntry? dtrEntry;
    private string objectExplorerStatus = "Ready.";
    private uint lastOwnedTreasureRoleInferenceDutyKey;
    private OwnershipMode lastOwnedTreasureRoleInferenceMode = OwnershipMode.Idle;
    private bool treasureDutyRecoveryAttemptedThisLoad;
    private readonly Queue<RemoteJsonReloadStep> pendingRemoteJsonReloadSteps = new();
    private DateTime nextRemoteJsonReloadDeferredLogUtc = DateTime.MinValue;
    private DateTime nextFrameworkSlowLogUtc = DateTime.MinValue;
    private double lastFrameworkSlowUpdateMs;
    private string lastFrameworkSlowUpdateSection = "none";
    private DateTime lastFrameworkSlowUpdateUtc = DateTime.MinValue;
    private FrameworkSlowUpdateContext? lastFrameworkSlowUpdateContext;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var configurationChanged = ApplyConfigurationMigrations(Configuration);
        if (configurationChanged)
            Configuration.Save();
        ECommonsMain.Init(PluginInterface, this, [ECommons.Module.VfxTracking]);
        VfxManager.EnableStaticVfxCreationTracking = true;
        VfxManager.Logging = false;
        VfxManager.LoggingFilter = string.Empty;

        var configDirectory = PluginInterface.GetPluginConfigDirectory();
        TreasureDungeonData.Configure(configDirectory, Log);
        TreasureDungeonRoleDetector = new TreasureDungeonRoleDetector(PluginInterface, ObjectTable, Log, configDirectory);
        TreasurePortalOpenerRelayService = new TreasurePortalOpenerRelayService(Log);
        TreasurePortalOpenerTracker = new TreasurePortalOpenerTracker(ObjectTable, PartyList, PlayerState, TreasurePortalOpenerRelayService, Log);
        BossModMultiboxFollowService = new BossModMultiboxFollowService(PluginInterface, CommandManager, Configuration, Log);
        TreasureFollowerAutoMoveAssistService = new TreasureFollowerAutoMoveAssistService(ObjectTable, PartyList, CommandManager, Log);
        RemoteJsonUpdateService = new RemoteJsonUpdateService(Log, configDirectory);
        RemoteJsonUpdateService.TryStartMissingUpdate("startup");

        DutyCatalogService = new DutyCatalogService(DataManager, Log, configDirectory);
        DutyContextService = new DutyContextService(ClientState, Condition, DutyCatalogService);
        ObjectPriorityRuleService = new ObjectPriorityRuleService(Log, DataManager, configDirectory);
        DialogYesNoRuleService = new DialogYesNoRuleService(Log, configDirectory);
        ObservationMemoryService = new ObservationMemoryService(ObjectTable, PartyList, Log, ObjectPriorityRuleService);
        DungeonFrontierService = new DungeonFrontierService(DataManager, ObjectTable, Log, ObjectPriorityRuleService, ObservationMemoryService);
        ObjectivePlannerService = new ObjectivePlannerService(ObjectTable, ObjectPriorityRuleService, DungeonFrontierService, ObservationMemoryService);
        MapFlagService = new MapFlagService(DataManager, ClientState, Condition, Log);
        ExecutionService = new ExecutionService(DataManager, ObjectTable, TargetManager, CommandManager, ObservationMemoryService, DungeonFrontierService, MapFlagService, ObjectPriorityRuleService, Configuration, Log);
        DialogAutomationService = new DialogAutomationService(GameGui, DialogYesNoRuleService, Log);
        TreasureHighLowDiagnosticService = new TreasureHighLowDiagnosticService(GameGui, ObjectTable, ClientState, DataManager, Log, Configuration, configDirectory);
        HigherLowerServerEventTraceService = new HigherLowerServerEventTraceService(ObjectTable, ClientState, PartyList, SigScanner, GameInteropProvider, TreasureHighLowDiagnosticService, Log);
        HigherLowerVfxTraceService = new HigherLowerVfxTraceService(ObjectTable, ClientState, TreasureHighLowDiagnosticService, Log);
        HigherLowerCardVfxSolverService = new HigherLowerCardVfxSolverService(TreasureHighLowDiagnosticService, HigherLowerVfxTraceService, HigherLowerServerEventTraceService, DataManager, Log);
        HigherLowerVfxTraceService.AttachCardSolver(HigherLowerCardVfxSolverService);
        HigherLowerAutomationService = new HigherLowerAutomationService(TreasureHighLowDiagnosticService, HigherLowerCardVfxSolverService, ObjectTable, TargetManager, CommandManager, Configuration, GameGui, Log);
        DebugStrafeService = new DebugStrafeService(KeyState, Log);
        InnEntryService = new InnEntryService(DataManager, ObjectTable, TargetManager, CommandManager, ClientState, Condition, Log);
        UtilityAutomationService = new UtilityAutomationService(DataManager, ObjectTable, TargetManager, CommandManager, ClientState, Condition, Log);
        LootAutomationService = new LootAutomationService(DataManager, CommandManager, SigScanner, Configuration, Log);
        TreasureFollowerDutyExitMonitorService = new TreasureFollowerDutyExitMonitorService(CommandManager, Log);
        BmrReflectionService = new BmrReflectionService(PluginInterface, Configuration, Log);
        AdsIpcService = new AdsIpcService(
            PluginInterface,
            StartDutyFromOutside,
            StartDutyFromInside,
            ResumeDutyFromInside,
            LeaveDuty,
            () =>
            {
                OpenLootUi();
                return true;
            },
            () =>
            {
                ToggleLootUi();
                return true;
            },
            StartRepair,
            GetStatusJson,
            GetCurrentAnalysisJson);
        ReflectionIpcService = new ReflectionIpcService(PluginInterface, BmrReflectionService);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        objectExplorerWindow = new ObjectExplorerWindow(this);
        ghostListWindow = new GhostListWindow(this);
        frontierLabelWindow = new FrontierLabelWindow(this);
        quickControlWindow = new QuickControlWindow(this);
        lootWindow = new LootWindow(this);
        objectRuleEditorWindow = new ObjectRuleEditorWindow(this);
        dialogRuleEditorWindow = new DialogRuleEditorWindow(this);
        higherLowerWindow = new HigherLowerWindow(this);
        treasureRouteEditorWindow = new TreasureRouteEditorWindow(this);
        serverEventExplorerWindow = new ServerEventExplorerWindow(this);
        vfxExplorerWindow = new VfxExplorerWindow(this);
        reflectionWindow = new ReflectionWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(objectExplorerWindow);
        WindowSystem.AddWindow(ghostListWindow);
        WindowSystem.AddWindow(frontierLabelWindow);
        WindowSystem.AddWindow(quickControlWindow);
        WindowSystem.AddWindow(lootWindow);
        WindowSystem.AddWindow(objectRuleEditorWindow);
        WindowSystem.AddWindow(dialogRuleEditorWindow);
        WindowSystem.AddWindow(higherLowerWindow);
        WindowSystem.AddWindow(treasureRouteEditorWindow);
        WindowSystem.AddWindow(serverEventExplorerWindow);
        WindowSystem.AddWindow(vfxExplorerWindow);
        WindowSystem.AddWindow(reflectionWindow);

        RegisterCommands();

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;
        DutyState.DutyCompleted += OnDutyCompleted;
        ChatGui.ChatMessage += OnChatMessage;

        SetupDtrBar();
        UpdateDtrBar();

        Log.Information($"[ADS] {RemoteJsonUpdateService.LastUpdateStatus}");
        Log.Information($"[ADS] Loaded version {PluginInfo.GetVersion()} from {PluginInterface.AssemblyLocation.FullName}");

        if (Configuration.OpenMainWindowOnLoad)
            OpenMainUi();
        if (Configuration.OpenQuickControlsOnLoad)
            quickControlWindow.IsOpen = true;

        Log.Information("[ADS] Plugin loaded.");
    }

    public void Dispose()
    {
        DebugStrafeService.Release("plugin dispose");
        ExecutionService.ReleaseHeldMovementKeys("plugin dispose");
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        DutyState.DutyCompleted -= OnDutyCompleted;
        ChatGui.ChatMessage -= OnChatMessage;

        InnEntryService.Cancel("plugin dispose");
        UtilityAutomationService.Cancel("plugin dispose");
        UnregisterCommands();
        HigherLowerServerEventTraceService.Dispose();
        HigherLowerVfxTraceService.Dispose();
        TreasureHighLowDiagnosticService.Dispose();
        AdsIpcService.Dispose();
        ReflectionIpcService.Dispose();
        BmrReflectionService.Dispose();
        TreasurePortalOpenerRelayService.Dispose();
        RemoteJsonUpdateService.Dispose();
        WindowSystem.RemoveAllWindows();
        dtrEntry?.Remove();
        configWindow.Dispose();
        mainWindow.Dispose();
        objectExplorerWindow.Dispose();
        ghostListWindow.Dispose();
        frontierLabelWindow.Dispose();
        quickControlWindow.Dispose();
        lootWindow.Dispose();
        objectRuleEditorWindow.Dispose();
        dialogRuleEditorWindow.Dispose();
        higherLowerWindow.Dispose();
        treasureRouteEditorWindow.Dispose();
        serverEventExplorerWindow.Dispose();
        vfxExplorerWindow.Dispose();
        reflectionWindow.Dispose();
        ECommonsMain.Dispose();
    }

    public void OpenMainUi()
        => mainWindow.IsOpen = true;

    public void OpenConfigUi()
        => configWindow.IsOpen = true;

    public void ToggleMainUi()
        => mainWindow.IsOpen = !mainWindow.IsOpen;

    public void ToggleObjectExplorerUi()
        => objectExplorerWindow.IsOpen = !objectExplorerWindow.IsOpen;

    public void OpenObjectExplorerUi()
        => objectExplorerWindow.IsOpen = true;

    public void ToggleGhostListUi()
        => ghostListWindow.IsOpen = !ghostListWindow.IsOpen;

    public void ToggleFrontierLabelUi()
        => frontierLabelWindow.IsOpen = !frontierLabelWindow.IsOpen;

    public void ToggleQuickControlUi()
    {
        if (quickControlWindow.IsOpen)
            DebugStrafeService.Release("mini close");

        quickControlWindow.IsOpen = !quickControlWindow.IsOpen;
    }

    public void ToggleLootUi()
        => lootWindow.IsOpen = !lootWindow.IsOpen;

    public void OpenLootUi()
        => lootWindow.IsOpen = true;

    public void OpenFrontierLabelUi()
        => frontierLabelWindow.IsOpen = true;

    public void ToggleRuleEditorUi()
        => objectRuleEditorWindow.IsOpen = !objectRuleEditorWindow.IsOpen;

    public void OpenRuleEditorUi()
        => objectRuleEditorWindow.IsOpen = true;

    public void ToggleDialogRuleEditorUi()
        => dialogRuleEditorWindow.IsOpen = !dialogRuleEditorWindow.IsOpen;

    public void OpenDialogRuleEditorUi()
        => dialogRuleEditorWindow.IsOpen = true;

    public void ToggleHigherLowerUi()
        => higherLowerWindow.IsOpen = !higherLowerWindow.IsOpen;

    public void OpenHigherLowerUi()
        => higherLowerWindow.IsOpen = true;

    public void ToggleTreasureRouteEditorUi()
        => treasureRouteEditorWindow.IsOpen = !treasureRouteEditorWindow.IsOpen;

    public void OpenTreasureRouteEditorUi()
        => treasureRouteEditorWindow.OpenForCurrentTerritory();

    public void ToggleServerEventExplorerUi()
        => serverEventExplorerWindow.IsOpen = !serverEventExplorerWindow.IsOpen;

    public void OpenServerEventExplorerUi()
        => serverEventExplorerWindow.IsOpen = true;

    public void ToggleVfxExplorerUi()
        => vfxExplorerWindow.IsOpen = !vfxExplorerWindow.IsOpen;

    public void OpenVfxExplorerUi()
        => vfxExplorerWindow.IsOpen = true;

    public void ToggleReflectionUi()
        => reflectionWindow.IsOpen = !reflectionWindow.IsOpen;

    public void OpenReflectionUi()
        => reflectionWindow.IsOpen = true;

    public void ToggleDebugStrafeLeft()
        => PrintStatus(DebugStrafeService.ToggleLeft(DutyContextService.Current.IsLoggedIn, Configuration.PluginEnabled));

    public void ToggleDebugStrafeRight()
        => PrintStatus(DebugStrafeService.ToggleRight(DutyContextService.Current.IsLoggedIn, Configuration.PluginEnabled));

    public void SaveConfiguration()
    {
        Configuration.Save();
        UpdateDtrBar();
    }

    public void ForceRemoteJsonUpdate()
        => RemoteJsonUpdateService.TryStartUpdate(force: true, "operator Update button");

    public void SetLootMode(LootRollMode mode)
    {
        if (Configuration.LootMode == mode)
        {
            PrintStatus($"Loot mode: {mode}.");
            return;
        }

        var previous = Configuration.LootMode;
        Configuration.LootMode = mode;
        SaveConfiguration();
        Log.Information($"[ADS][Loot] Loot mode {previous} -> {mode}.");
        PrintStatus($"Loot mode: {mode}.");
    }

    public void SetLootRegistrableNeedingEnabled(bool enabled, bool printStatus = false)
    {
        if (Configuration.LootRegistrableNeedingEnabled == enabled)
        {
            if (printStatus)
                PrintStatus($"Loot registrable Need missing: {(enabled ? "ON" : "OFF")}.");
            return;
        }

        Configuration.LootRegistrableNeedingEnabled = enabled;
        SaveConfiguration();
        if (printStatus)
            PrintStatus($"Loot registrable Need missing: {(enabled ? "ON" : "OFF")}.");
    }

    public void SetLootRegistrableMountsEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableMountsEnabled == enabled)
            return;

        Configuration.LootRegistrableMountsEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableMinionsEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableMinionsEnabled == enabled)
            return;

        Configuration.LootRegistrableMinionsEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableFashionAccessoriesEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableFashionAccessoriesEnabled == enabled)
            return;

        Configuration.LootRegistrableFashionAccessoriesEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableFacewearEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableFacewearEnabled == enabled)
            return;

        Configuration.LootRegistrableFacewearEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableOrchestrionRollsEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableOrchestrionRollsEnabled == enabled)
            return;

        Configuration.LootRegistrableOrchestrionRollsEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableFadedOrchestrionCopiesEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableFadedOrchestrionCopiesEnabled == enabled)
            return;

        Configuration.LootRegistrableFadedOrchestrionCopiesEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableEmotesHairstylesEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableEmotesHairstylesEnabled == enabled)
            return;

        Configuration.LootRegistrableEmotesHairstylesEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableBardingsEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableBardingsEnabled == enabled)
            return;

        Configuration.LootRegistrableBardingsEnabled = enabled;
        SaveConfiguration();
    }

    public void SetLootRegistrableTripleTriadCardsEnabled(bool enabled)
    {
        if (Configuration.LootRegistrableTripleTriadCardsEnabled == enabled)
            return;

        Configuration.LootRegistrableTripleTriadCardsEnabled = enabled;
        SaveConfiguration();
    }

    public void ResetWindowPositions()
    {
        mainWindow.QueueResetToOrigin();
        configWindow.QueueResetToOrigin();
        objectExplorerWindow.QueueResetToOrigin();
        ghostListWindow.QueueResetToOrigin();
        frontierLabelWindow.QueueResetToOrigin();
        quickControlWindow.QueueResetToOrigin();
        lootWindow.QueueResetToOrigin();
        objectRuleEditorWindow.QueueResetToOrigin();
        dialogRuleEditorWindow.QueueResetToOrigin();
        higherLowerWindow.QueueResetToOrigin();
        treasureRouteEditorWindow.QueueResetToOrigin();
        serverEventExplorerWindow.QueueResetToOrigin();
        vfxExplorerWindow.QueueResetToOrigin();
        reflectionWindow.QueueResetToOrigin();
    }

    public void JumpWindows()
    {
        mainWindow.QueueRandomVisibleJump();
        configWindow.QueueRandomVisibleJump();
        objectExplorerWindow.QueueRandomVisibleJump();
        ghostListWindow.QueueRandomVisibleJump();
        frontierLabelWindow.QueueRandomVisibleJump();
        quickControlWindow.QueueRandomVisibleJump();
        lootWindow.QueueRandomVisibleJump();
        objectRuleEditorWindow.QueueRandomVisibleJump();
        dialogRuleEditorWindow.QueueRandomVisibleJump();
        higherLowerWindow.QueueRandomVisibleJump();
        treasureRouteEditorWindow.QueueRandomVisibleJump();
        serverEventExplorerWindow.QueueRandomVisibleJump();
        vfxExplorerWindow.QueueRandomVisibleJump();
        reflectionWindow.QueueRandomVisibleJump();
    }

    public string ObjectExplorerStatus
        => objectExplorerStatus;

    public bool TryPlaceObjectFlag(string objectName, System.Numerics.Vector3 worldPosition)
    {
        var territoryId = DutyContextService.Current.TerritoryTypeId != 0
            ? DutyContextService.Current.TerritoryTypeId
            : ClientState.TerritoryType;

        var result = MapFlagService.TryPlaceFlag(territoryId, worldPosition, objectName, out var status);
        objectExplorerStatus = status;
        return result;
    }

    public bool TryExplorerNavigation(System.Numerics.Vector3 worldPosition, bool useFly)
    {
        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"{(useFly ? "/vnav flyto" : "/vnav moveto")} {worldPosition.X:0.00} {worldPosition.Y:0.00} {worldPosition.Z:0.00}");
        try
        {
            var result = CommandManager.ProcessCommand(command);
            objectExplorerStatus = result
                ? $"Sent {(useFly ? "flyto" : "moveto")} to {worldPosition.X:0.00}, {worldPosition.Y:0.00}, {worldPosition.Z:0.00}."
                : $"Failed to send {command}.";
            return result;
        }
        catch (Exception ex)
        {
            objectExplorerStatus = $"Explorer navigation failed: {ex.Message}";
            Log.Warning(ex, $"[ADS] Explorer navigation command failed: {command}");
            return false;
        }
    }

    public void CreateRuleFromExplorer(string objectName, string objectKind, uint baseId, System.Numerics.Vector3 worldPosition, string classificationOverride = "")
    {
        var context = DutyContextService.Current;
        var seededRule = ObjectPriorityRuleService.CreateBlankRule();
        seededRule.DutyEnglishName = context.CurrentDuty?.EnglishName ?? string.Empty;
        seededRule.TerritoryTypeId = context.TerritoryTypeId;
        seededRule.ContentFinderConditionId = context.ContentFinderConditionId;
        seededRule.ObjectKind = objectKind;
        seededRule.BaseId = baseId;
        seededRule.ObjectName = objectName;
        seededRule.NameMatchMode = "Exact";
        seededRule.Classification = classificationOverride;
        seededRule.Layer = context.InInstancedDuty
            ? ObjectPriorityRuleService.GetActiveLayerName(context) ?? string.Empty
            : string.Empty;
        var classificationNote = string.IsNullOrWhiteSpace(classificationOverride)
            ? "Auto classification left blank."
            : $"Classification override: {classificationOverride}.";
        seededRule.Notes = context.InInstancedDuty
            ? $"Seeded from Object Explorer at {worldPosition.X:0.0},{worldPosition.Y:0.0},{worldPosition.Z:0.0} on layer {seededRule.Layer}. {classificationNote}"
            : $"Seeded from Object Explorer at {worldPosition.X:0.0},{worldPosition.Y:0.0},{worldPosition.Z:0.0}. {classificationNote}";

        objectRuleEditorWindow.CreateRuleFromExplorer(seededRule);
        objectRuleEditorWindow.IsOpen = true;
    }

    public bool StartDutyFromOutside()
    {
        QueueDutyOwnershipRemoteUpdate();
        ResetOwnedTreasureRoleInferenceLatch();
        InferAndApplyTreasureDungeonRole("outside start");
        TreasurePortalOpenerTracker.BeginEntryCycle("outside start");
        TreasurePortalOpenerRelayService.Clear("new treasure cycle");
        var result = ExecutionService.StartDutyFromOutside();
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool StartDutyFromInside()
    {
        QueueDutyOwnershipRemoteUpdate();
        TreasurePortalOpenerTracker.BeginEntryCycle("inside start", preserveRecentDirectOpener: true);
        InferAndApplyTreasureDungeonRole("inside start", resetFollowerProgressForOwnership: true);
        var result = ExecutionService.StartDutyFromInside(DutyContextService.Current);
        if (result)
        {
            RememberOwnedTreasureRoleInference(DutyContextService.Current, OwnershipMode.OwnedStartInside);
            WriteTreasureDutyRecoveryMarker(DutyContextService.Current, "inside start", force: true);
        }
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool ResumeDutyFromInside()
    {
        QueueDutyOwnershipRemoteUpdate();
        TreasurePortalOpenerTracker.BeginEntryCycle("inside resume", preserveRecentDirectOpener: true);
        InferAndApplyTreasureDungeonRole("inside resume");
        var result = ExecutionService.ResumeDutyFromInside(DutyContextService.Current);
        if (result)
        {
            RememberOwnedTreasureRoleInference(DutyContextService.Current, OwnershipMode.OwnedResumeInside);
            WriteTreasureDutyRecoveryMarker(DutyContextService.Current, "inside resume", force: true);
        }
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool LeaveDuty()
    {
        var shouldClearTreasureFollow =
            ExecutionService.TreasureDungeonRole == TreasureDungeonRole.Follower ||
            BossModMultiboxFollowService.FollowerMovementOwnedByBmrai ||
            BossModMultiboxFollowService.BmraiFollowCommandAccepted == true ||
            BossModMultiboxFollowService.CleanupPending;

        var result = ExecutionService.LeaveDuty(DutyContextService.Current, Configuration.ConsiderTreasureCoffers);
        TreasurePortalOpenerTracker.ClearPendingOpener("leave duty");
        TreasurePortalOpenerRelayService.Clear("leave duty");
        BossModMultiboxFollowService.Clear("leave duty request", shouldClearTreasureFollow);
        if (result)
            TreasureFollowerDutyExitMonitorService.Disarm("ADS leave duty request");
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool StartInnEntry()
    {
        var result = InnEntryService.StartManualEntry();
        PrintStatus(result ? InnEntryService.StatusMessage : $"Inn entry not started: {InnEntryService.StatusMessage}");
        return result;
    }

    public void StopOwnership()
    {
        var stoppedInn = InnEntryService.IsRunning;
        var stoppedUtility = UtilityAutomationService.IsRunning;
        DebugStrafeService.Release("ADS stop");
        ExecutionService.Stop(DutyContextService.Current);
        ResetOwnedTreasureRoleInferenceLatch();
        ClearTreasureDutyRecoveryMarker("ownership stop");
        TreasurePortalOpenerTracker.ClearPendingOpener("ownership stop");
        TreasurePortalOpenerRelayService.Clear("ownership stop");
        BossModMultiboxFollowService.Clear("ownership stop");
        TreasureFollowerDutyExitMonitorService.Disarm("ownership stop");
        InnEntryService.Cancel("operator stop");
        UtilityAutomationService.Cancel("operator stop");
        var stoppedText = stoppedInn || stoppedUtility
            ? $" Stopped manual automation: {string.Join(", ", new[]
            {
                stoppedInn ? "enterinn" : string.Empty,
                stoppedUtility ? "utility" : string.Empty,
            }.Where(static value => !string.IsNullOrWhiteSpace(value)))}."
            : string.Empty;
        PrintStatus($"{ExecutionService.LastStatus}{stoppedText}");
        UpdateDtrBar();
    }

    public bool StartSelfRepair()
    {
        if (!CanStartManualUtility("self-repair"))
            return false;

        var result = UtilityAutomationService.StartSelfRepair();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"Self-repair not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public bool StartNpcRepair()
    {
        if (!CanStartManualUtility("NPC repair"))
            return false;

        var result = UtilityAutomationService.StartNpcRepair();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"NPC repair not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public bool StartNpcRepairNoInn()
    {
        if (!CanStartManualUtility("NPC repair without inn fallback"))
            return false;

        var result = UtilityAutomationService.StartNpcRepairNoInn();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"NPC repair not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public bool StartNpcRepairNoTeleportNoInn()
    {
        if (!CanStartManualUtility("NPC repair without inn fallback or teleport"))
            return false;

        var result = UtilityAutomationService.StartNpcRepairNoTeleportNoInn();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"NPC repair not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public bool StartRepair(string mode)
    {
        var normalized = NormalizeRepairMode(mode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            PrintStatus("Repair mode must be self, npc, npc-no-inn, or npc-no-teleport-no-inn.");
            return false;
        }

        return normalized switch
        {
            "self" => StartSelfRepair(),
            "npc" => StartNpcRepair(),
            "npc-no-inn" => StartNpcRepairNoInn(),
            "npc-no-teleport-no-inn" => StartNpcRepairNoTeleportNoInn(),
            _ => false,
        };
    }

    public bool StartExtractMateria()
    {
        if (!CanStartManualUtility("materia extraction"))
            return false;

        var result = UtilityAutomationService.StartExtractMateria();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"Materia extraction not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public bool StartDesynthFromInventory()
    {
        if (!CanStartManualUtility("inventory desynthesis"))
            return false;

        var result = UtilityAutomationService.StartDesynthFromInventory();
        PrintStatus(result ? UtilityAutomationService.StatusMessage : $"Inventory desynthesis not started: {UtilityAutomationService.StatusMessage}");
        return result;
    }

    public string GetStatusJson()
        => JsonSerializer.Serialize(
            new
            {
                pluginEnabled = Configuration.PluginEnabled,
                lootMode = Configuration.LootMode.ToString(),
                lootRegistrableNeedingEnabled = Configuration.LootRegistrableNeedingEnabled,
                lootStatus = LootAutomationService.Status,
                processDialogRulesOutsideOwnedDuty = Configuration.ProcessDialogRulesOutsideOwnedDuty,
                higherLowerVfxDataminingEnabled = Configuration.HigherLowerVfxDataminingEnabled,
                reflection = BmrReflectionService.CaptureStatusPayload(),
                version = PluginInfo.GetVersion(),
                lastFrameworkSlowUpdateMs = lastFrameworkSlowUpdateUtc == DateTime.MinValue
                    ? null
                    : (double?)lastFrameworkSlowUpdateMs,
                lastFrameworkSlowUpdateSection = lastFrameworkSlowUpdateUtc == DateTime.MinValue
                    ? string.Empty
                    : lastFrameworkSlowUpdateSection,
                lastFrameworkSlowUpdateUtc = lastFrameworkSlowUpdateUtc == DateTime.MinValue
                    ? null
                    : lastFrameworkSlowUpdateUtc.ToString("O"),
                lastFrameworkSlowUpdateContext,
                ownershipMode = ExecutionService.CurrentMode.ToString(),
                executionPhase = ExecutionService.CurrentPhase.ToString(),
                executionStatus = ExecutionService.LastStatus,
                treasureDungeonRole = ExecutionService.TreasureDungeonRoleDisplayName,
                treasureDungeonRoleBehavior = ExecutionService.TreasureDungeonRole.ToString(),
                effectiveTreasureDungeonRole = DungeonFrontierService.EffectiveTreasureDungeonRole.ToString(),
                treasureDungeonRoleSource = ExecutionService.TreasureDungeonRoleSource,
                treasureDungeonRoleDetail = ExecutionService.TreasureDungeonRoleDetail,
                treasurePortalOpenerSource = TreasurePortalOpenerTracker.Current?.Source ?? string.Empty,
                treasurePortalOpenerName = TreasurePortalOpenerTracker.Current?.OpenerName ?? string.Empty,
                treasurePortalOpenerPartySlot = TreasurePortalOpenerTracker.Current?.PartySlot,
                treasurePortalOpenerObjectId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.GameObjectId),
                treasurePortalOpenerEntityId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.EntityId),
                treasurePortalOpenerContentId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.ContentId),
                treasurePortalOpenerAgeSeconds = TreasurePortalOpenerTracker.CurrentAgeSeconds,
                treasureFollowTargetName = TreasurePortalOpenerTracker.Current?.OpenerName ?? string.Empty,
                treasureFollowTargetSlot = TreasurePortalOpenerTracker.Current?.PartySlot,
                treasureFollowTargetSource = TreasurePortalOpenerTracker.Current?.Source ?? string.Empty,
                treasureFollowTargetContentId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.ContentId),
                treasureFollowTargetLocal = TreasurePortalOpenerTracker.Current?.IsLocalOpener,
                treasurePortalInteractionWitnessSource = TreasurePortalOpenerTracker.LastInteractionWitnessSource,
                treasurePortalInteractionWitnessName = TreasurePortalOpenerTracker.LastInteractionWitnessName,
                treasurePortalInteractionWitnessTarget = TreasurePortalOpenerTracker.LastInteractionWitnessTarget,
                treasurePortalInteractionWitnessAgeSeconds = TreasurePortalOpenerTracker.LastInteractionWitnessAgeSeconds,
                treasurePortalRelayStatus = TreasurePortalOpenerTracker.RelayStatus,
                treasurePortalFallbackEligibleAtUtc = TreasurePortalOpenerTracker.FallbackEligibleAtUtc?.ToString("O"),
                treasurePortalFallbackRemainingSeconds = TreasurePortalOpenerTracker.FallbackRemainingSeconds,
                treasurePortalFallbackReason = TreasurePortalOpenerTracker.FallbackReason,
                treasurePortalFollowApplied = BossModMultiboxFollowService.FollowApplied,
                treasurePortalFollowLeaderContentId = FormatOptionalId(BossModMultiboxFollowService.FollowLeaderContentId),
                treasurePortalFollowMethod = BossModMultiboxFollowService.FollowMethod,
                treasurePortalFollowStatus = BossModMultiboxFollowService.FollowStatus,
                bmraiFollowCommandMethod = BossModMultiboxFollowService.BmraiFollowCommandMethod,
                bmraiFollowCommandText = BossModMultiboxFollowService.BmraiFollowCommandText,
                bmraiFollowCommandAccepted = BossModMultiboxFollowService.BmraiFollowCommandAccepted,
                bmraiFollowCommandAtUtc = BossModMultiboxFollowService.BmraiFollowCommandAtUtc?.ToString("O"),
                bmraiFollowCommandStatus = BossModMultiboxFollowService.BmraiFollowCommandStatus,
                bmraiFollowCommandTargetName = BossModMultiboxFollowService.BmraiFollowCommandTargetName,
                bmraiFollowCommandTargetSlot = BossModMultiboxFollowService.BmraiFollowCommandTargetSlot,
                bmraiFollowCommandTargetContentId = FormatOptionalId(BossModMultiboxFollowService.BmraiFollowCommandTargetContentId),
                bmraiFollowCommandTargetSource = BossModMultiboxFollowService.BmraiFollowCommandTargetSource,
                treasureFollowerMovementOwnedByBmrai = BossModMultiboxFollowService.FollowerMovementOwnedByBmrai,
                treasureFollowerMovementStatus = BossModMultiboxFollowService.FollowerMovementStatus,
                treasureFollowerAutoMoveAssistStatus = TreasureFollowerAutoMoveAssistService.Status,
                treasureFollowerAutoMoveAssistTargetName = TreasureFollowerAutoMoveAssistService.TargetName,
                treasureFollowerAutoMoveAssistDistanceXz = TreasureFollowerAutoMoveAssistService.DistanceXz,
                treasureFollowerAutoMoveAssistCommandSentAtUtc = TreasureFollowerAutoMoveAssistService.CommandSentAtUtc?.ToString("O"),
                treasureDutyRecoveryKey = Configuration.TreasureDutyRecoveryKey,
                treasureDutyRecoveryUtc = Configuration.TreasureDutyRecoveryUtc == DateTime.MinValue
                    ? string.Empty
                    : Configuration.TreasureDutyRecoveryUtc.ToString("O"),
                treasureDutyRecoveryRole = Configuration.TreasureDutyRecoveryRole,
                bmraiTreasureFollowCleanupPending = Configuration.BmraiTreasureFollowCleanupPending,
                treasureFollowerDutyExitMonitorArmed = TreasureFollowerDutyExitMonitorService.Armed,
                treasureFollowerDutyExitMonitorCleanupSent = TreasureFollowerDutyExitMonitorService.CleanupSent,
                treasureFollowerDutyExitMonitorStatus = TreasureFollowerDutyExitMonitorService.Status,
                treasureFollowerDutyExitMonitorArmedAtUtc = TreasureFollowerDutyExitMonitorService.ArmedAtUtc?.ToString("O"),
                treasureFollowerDutyExitMonitorCleanupSentAtUtc = TreasureFollowerDutyExitMonitorService.CleanupSentAtUtc?.ToString("O"),
                treasureFollowerDutyExitMonitorDutyKey = TreasureFollowerDutyExitMonitorService.DutyKey,
                treasureFollowerDutyExitMonitorSource = TreasureFollowerDutyExitMonitorService.Source,
                frontierRouteSource = DungeonFrontierService.CurrentTreasureRouteSource,
                frontierRouteKey = DungeonFrontierService.CurrentTarget?.Key,
                treasureFollowerRouteHoldReason = DungeonFrontierService.TreasureFollowerRouteHoldReason,
                treasureFollowerEntryMapOpenerRoleActive = DungeonFrontierService.TreasureFollowerEntryMapOpenerRoleActive,
                treasureFollowerEntryProofDutyKey = DungeonFrontierService.TreasureFollowerEntryProofDutyKey,
                treasureFollowerHeldCandidateKey = DungeonFrontierService.TreasureFollowerHeldCandidateKey,
                treasureFollowerHeldCandidateName = DungeonFrontierService.TreasureFollowerHeldCandidateName,
                treasureFollowerHeldCandidateTransitObserved = DungeonFrontierService.TreasureFollowerHeldCandidateTransitObserved,
                treasureFollowerLastFailedCandidateKey = DungeonFrontierService.TreasureFollowerLastFailedCandidateKey,
                treasureFollowerLastFailedCandidateReason = DungeonFrontierService.TreasureFollowerLastFailedCandidateReason,
                treasureFollowerRoomProofSource = DungeonFrontierService.TreasureFollowerRoomProofSource,
                treasureFollowerDoorAttemptStage = DungeonFrontierService.TreasureFollowerDoorAttemptStage,
                treasureFollowerDoorAttemptRoom = DungeonFrontierService.TreasureFollowerDoorAttemptRoom,
                treasureFollowerDoorAttemptGroup = DungeonFrontierService.TreasureFollowerDoorAttemptGroup,
                treasureFollowerDoorChaseGateState = DungeonFrontierService.TreasureFollowerDoorChaseGateState,
                treasureFollowerDoorChaseGateRoomIndex = DungeonFrontierService.TreasureFollowerDoorChaseGateRoomIndex,
                treasureFollowerDoorChaseGateTransitionSeenActive = DungeonFrontierService.TreasureFollowerDoorChaseGateTransitionSeenActive,
                treasureFollowerDoorChaseGateSettleRemainingSeconds = DungeonFrontierService.TreasureFollowerDoorChaseGateSettleRemainingSeconds,
                treasureFollowerDoorChaseHoldActive = DungeonFrontierService.TreasureFollowerDoorChaseHoldActive,
                treasureFollowerRoomRetryCooldownRemainingSeconds = DungeonFrontierService.TreasureFollowerRoomRetryCooldownRemainingSeconds,
                treasureFollowerCofferSeekRoomIndex = DungeonFrontierService.TreasureFollowerCofferSeekRoomIndex,
                treasureFollowerCofferSeekState = DungeonFrontierService.TreasureFollowerCofferSeekStateName,
                treasureFollowerCofferSeekTargetKey = DungeonFrontierService.TreasureFollowerCofferSeekTargetKey,
                treasureFollowerCofferSeekTargetName = DungeonFrontierService.TreasureFollowerCofferSeekTargetName,
                treasureFollowerCofferSeekTargetPosition = DungeonFrontierService.TreasureFollowerCofferSeekTargetPosition is { } cofferSeekPosition
                    ? BuildPositionPayload(cofferSeekPosition)
                    : null,
                treasureFollowerCofferSeekReached = DungeonFrontierService.TreasureFollowerCofferSeekReached,
                treasureFollowerCofferSeekAttempted = DungeonFrontierService.TreasureFollowerCofferSeekAttempted,
                treasureFollowerCofferSeekLastReason = DungeonFrontierService.TreasureFollowerCofferSeekLastReason,
                treasureFollowerDoorFollowThroughActive = ExecutionService.TreasureFollowerDoorFollowThroughActive,
                treasureFollowerDoorFollowThroughCandidateKey = ExecutionService.TreasureFollowerDoorFollowThroughCandidateKey,
                treasureFollowerDoorFollowThroughCandidateName = ExecutionService.TreasureFollowerDoorFollowThroughCandidateName,
                treasureFollowerDoorFollowThroughTarget = ExecutionService.TreasureFollowerDoorFollowThroughTarget,
                treasureFollowerDoorFollowThroughStage = ExecutionService.TreasureFollowerDoorFollowThroughStage,
                treasureFollowerPostTransitSettleRemainingSeconds = ExecutionService.TreasureFollowerPostTransitSettleRemainingSeconds,
                liveTreasureDoorCandidateCount = DungeonFrontierService.LiveTreasureDoorCandidateCount,
                dialogVisible = DialogAutomationService.DialogVisible,
                dialogPrompt = DialogAutomationService.DialogPrompt,
                dialogRule = DialogAutomationService.DialogRule,
                dialogStatus = DialogAutomationService.DialogStatus,
                dialogLastAction = DialogAutomationService.DialogLastAction,
                dialogLastFailure = DialogAutomationService.DialogLastFailure,
                dialogLastActionAtUtc = DialogAutomationService.DialogLastActionAtUtc == DateTime.MinValue
                    ? null
                    : DialogAutomationService.DialogLastActionAtUtc.ToString("O"),
                higherLowerAutomation = HigherLowerAutomationService.CaptureDebugState(),
                manualDestinationTarget = ExecutionService.CurrentManualDestinationTarget,
                manualDestinationDistance = ExecutionService.CurrentManualDestinationDistance,
                manualDestinationLastProgressAgeSeconds = ExecutionService.ManualDestinationLastProgressAgeSeconds,
                manualDestinationLastGhostReason = DungeonFrontierService.LastGhostedManualDestinationReason,
                utilityRunning = UtilityAutomationService.IsRunning,
                utilitySuppressesGenericYesNo = UtilityAutomationService.SuppressesGenericYesNo,
                utilityTask = UtilityAutomationService.ActiveTaskName,
                utilityMode = UtilityAutomationService.ActiveModeName,
                utilityStatus = UtilityAutomationService.StatusMessage,
                utilityLastSuccess = UtilityAutomationService.LastSuccessMessage,
                utilityLastFailure = UtilityAutomationService.LastFailureMessage,
                utilityCompletedAtUtc = UtilityAutomationService.LastCompletionUtc == DateTime.MinValue
                    ? null
                    : UtilityAutomationService.LastCompletionUtc.ToString("O"),
                duty = DutyContextService.Current.CurrentDuty?.EnglishName,
                territoryTypeId = DutyContextService.Current.TerritoryTypeId,
                mapId = DutyContextService.Current.MapId,
                contentFinderConditionId = DutyContextService.Current.ContentFinderConditionId,
                inInstancedDuty = DutyContextService.Current.InInstancedDuty,
                hasCatalogMetadata = DutyContextService.Current.HasCatalogMetadata,
                dutyCategory = DutyContextService.Current.CurrentDuty?.Category.ToString(),
                supportLevel = DutyContextService.Current.CurrentDuty?.SupportLevel.ToString(),
                clearanceStatus = DutyContextService.Current.CurrentDuty?.ClearanceStatus.ToString(),
                unsafeTransition = DutyContextService.Current.IsUnsafeTransition,
                mounted = DutyContextService.Current.Mounted,
            },
            JsonOptions);

    public string GetCurrentAnalysisJson()
        => JsonSerializer.Serialize(
            new
            {
                plannerMode = ObjectivePlannerService.Current.Mode.ToString(),
                objectiveKind = ObjectivePlannerService.Current.ObjectiveKind.ToString(),
                objective = ObjectivePlannerService.Current.Objective,
                explanation = ObjectivePlannerService.Current.Explanation,
                executionPhase = ExecutionService.CurrentPhase.ToString(),
                executionStatus = ExecutionService.LastStatus,
                treasureDungeonRole = ExecutionService.TreasureDungeonRoleDisplayName,
                treasureDungeonRoleBehavior = ExecutionService.TreasureDungeonRole.ToString(),
                treasureDungeonRoleSource = ExecutionService.TreasureDungeonRoleSource,
                treasureDungeonRoleDetail = ExecutionService.TreasureDungeonRoleDetail,
                treasurePortalOpenerSource = TreasurePortalOpenerTracker.Current?.Source ?? string.Empty,
                treasurePortalOpenerName = TreasurePortalOpenerTracker.Current?.OpenerName ?? string.Empty,
                treasurePortalOpenerPartySlot = TreasurePortalOpenerTracker.Current?.PartySlot,
                treasurePortalOpenerObjectId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.GameObjectId),
                treasurePortalOpenerEntityId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.EntityId),
                treasurePortalOpenerContentId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.ContentId),
                treasurePortalOpenerAgeSeconds = TreasurePortalOpenerTracker.CurrentAgeSeconds,
                treasureFollowTargetName = TreasurePortalOpenerTracker.Current?.OpenerName ?? string.Empty,
                treasureFollowTargetSlot = TreasurePortalOpenerTracker.Current?.PartySlot,
                treasureFollowTargetSource = TreasurePortalOpenerTracker.Current?.Source ?? string.Empty,
                treasureFollowTargetContentId = FormatOptionalId(TreasurePortalOpenerTracker.Current?.ContentId),
                treasureFollowTargetLocal = TreasurePortalOpenerTracker.Current?.IsLocalOpener,
                treasurePortalInteractionWitnessSource = TreasurePortalOpenerTracker.LastInteractionWitnessSource,
                treasurePortalInteractionWitnessName = TreasurePortalOpenerTracker.LastInteractionWitnessName,
                treasurePortalInteractionWitnessTarget = TreasurePortalOpenerTracker.LastInteractionWitnessTarget,
                treasurePortalInteractionWitnessAgeSeconds = TreasurePortalOpenerTracker.LastInteractionWitnessAgeSeconds,
                treasurePortalRelayStatus = TreasurePortalOpenerTracker.RelayStatus,
                treasurePortalFallbackEligibleAtUtc = TreasurePortalOpenerTracker.FallbackEligibleAtUtc?.ToString("O"),
                treasurePortalFallbackRemainingSeconds = TreasurePortalOpenerTracker.FallbackRemainingSeconds,
                treasurePortalFallbackReason = TreasurePortalOpenerTracker.FallbackReason,
                treasurePortalFollowApplied = BossModMultiboxFollowService.FollowApplied,
                treasurePortalFollowLeaderContentId = FormatOptionalId(BossModMultiboxFollowService.FollowLeaderContentId),
                treasurePortalFollowMethod = BossModMultiboxFollowService.FollowMethod,
                treasurePortalFollowStatus = BossModMultiboxFollowService.FollowStatus,
                bmraiFollowCommandMethod = BossModMultiboxFollowService.BmraiFollowCommandMethod,
                bmraiFollowCommandText = BossModMultiboxFollowService.BmraiFollowCommandText,
                bmraiFollowCommandAccepted = BossModMultiboxFollowService.BmraiFollowCommandAccepted,
                bmraiFollowCommandAtUtc = BossModMultiboxFollowService.BmraiFollowCommandAtUtc?.ToString("O"),
                bmraiFollowCommandStatus = BossModMultiboxFollowService.BmraiFollowCommandStatus,
                bmraiFollowCommandTargetName = BossModMultiboxFollowService.BmraiFollowCommandTargetName,
                bmraiFollowCommandTargetSlot = BossModMultiboxFollowService.BmraiFollowCommandTargetSlot,
                bmraiFollowCommandTargetContentId = FormatOptionalId(BossModMultiboxFollowService.BmraiFollowCommandTargetContentId),
                bmraiFollowCommandTargetSource = BossModMultiboxFollowService.BmraiFollowCommandTargetSource,
                treasureFollowerMovementOwnedByBmrai = BossModMultiboxFollowService.FollowerMovementOwnedByBmrai,
                treasureFollowerMovementStatus = BossModMultiboxFollowService.FollowerMovementStatus,
                treasureFollowerAutoMoveAssistStatus = TreasureFollowerAutoMoveAssistService.Status,
                treasureFollowerAutoMoveAssistTargetName = TreasureFollowerAutoMoveAssistService.TargetName,
                treasureFollowerAutoMoveAssistDistanceXz = TreasureFollowerAutoMoveAssistService.DistanceXz,
                treasureFollowerAutoMoveAssistCommandSentAtUtc = TreasureFollowerAutoMoveAssistService.CommandSentAtUtc?.ToString("O"),
                treasureDutyRecoveryKey = Configuration.TreasureDutyRecoveryKey,
                treasureDutyRecoveryUtc = Configuration.TreasureDutyRecoveryUtc == DateTime.MinValue
                    ? string.Empty
                    : Configuration.TreasureDutyRecoveryUtc.ToString("O"),
                treasureDutyRecoveryRole = Configuration.TreasureDutyRecoveryRole,
                bmraiTreasureFollowCleanupPending = Configuration.BmraiTreasureFollowCleanupPending,
                treasureFollowerDutyExitMonitorArmed = TreasureFollowerDutyExitMonitorService.Armed,
                treasureFollowerDutyExitMonitorCleanupSent = TreasureFollowerDutyExitMonitorService.CleanupSent,
                treasureFollowerDutyExitMonitorStatus = TreasureFollowerDutyExitMonitorService.Status,
                dialogVisible = DialogAutomationService.DialogVisible,
                dialogPrompt = DialogAutomationService.DialogPrompt,
                dialogRule = DialogAutomationService.DialogRule,
                dialogStatus = DialogAutomationService.DialogStatus,
                higherLowerVfxDataminingEnabled = Configuration.HigherLowerVfxDataminingEnabled,
                higherLowerAutomation = HigherLowerAutomationService.CaptureDebugState(),
                mounted = DutyContextService.Current.Mounted,
                targetName = ObjectivePlannerService.Current.TargetName,
                targetDistance = ObjectivePlannerService.Current.TargetDistance,
                targetVerticalDelta = ObjectivePlannerService.Current.TargetVerticalDelta,
                capturedAtUtc = ObjectivePlannerService.Current.CapturedAtUtc,
                mapId = DutyContextService.Current.MapId,
                frontier = new
                {
                    mode = DungeonFrontierService.CurrentMode.ToString(),
                    treasureDungeonRole = DungeonFrontierService.TreasureDungeonRoleDisplayName,
                    treasureDungeonRoleBehavior = DungeonFrontierService.TreasureDungeonRole.ToString(),
                    effectiveTreasureDungeonRole = DungeonFrontierService.EffectiveTreasureDungeonRole.ToString(),
                    treasureDungeonRoleSource = DungeonFrontierService.TreasureDungeonRoleSource,
                    treasureDungeonRoleDetail = DungeonFrontierService.TreasureDungeonRoleDetail,
                    treasureFollowerRetryCycle = DungeonFrontierService.TreasureFollowerRetryCycle,
                    treasureFollowerRouteHoldReason = DungeonFrontierService.TreasureFollowerRouteHoldReason,
                    treasureFollowerEntryMapOpenerRoleActive = DungeonFrontierService.TreasureFollowerEntryMapOpenerRoleActive,
                    treasureFollowerEntryProofDutyKey = DungeonFrontierService.TreasureFollowerEntryProofDutyKey,
                    treasureFollowerHeldCandidateKey = DungeonFrontierService.TreasureFollowerHeldCandidateKey,
                    treasureFollowerHeldCandidateName = DungeonFrontierService.TreasureFollowerHeldCandidateName,
                    treasureFollowerHeldCandidateTransitObserved = DungeonFrontierService.TreasureFollowerHeldCandidateTransitObserved,
                    treasureFollowerLastFailedCandidateKey = DungeonFrontierService.TreasureFollowerLastFailedCandidateKey,
                    treasureFollowerLastFailedCandidateReason = DungeonFrontierService.TreasureFollowerLastFailedCandidateReason,
                    treasureFollowerRoomProofSource = DungeonFrontierService.TreasureFollowerRoomProofSource,
                    treasureFollowerDoorAttemptStage = DungeonFrontierService.TreasureFollowerDoorAttemptStage,
                    treasureFollowerDoorAttemptRoom = DungeonFrontierService.TreasureFollowerDoorAttemptRoom,
                    treasureFollowerDoorAttemptGroup = DungeonFrontierService.TreasureFollowerDoorAttemptGroup,
                    treasureFollowerDoorChaseGateState = DungeonFrontierService.TreasureFollowerDoorChaseGateState,
                    treasureFollowerDoorChaseGateRoomIndex = DungeonFrontierService.TreasureFollowerDoorChaseGateRoomIndex,
                    treasureFollowerDoorChaseGateTransitionSeenActive = DungeonFrontierService.TreasureFollowerDoorChaseGateTransitionSeenActive,
                    treasureFollowerDoorChaseGateSettleRemainingSeconds = DungeonFrontierService.TreasureFollowerDoorChaseGateSettleRemainingSeconds,
                    treasureFollowerDoorChaseHoldActive = DungeonFrontierService.TreasureFollowerDoorChaseHoldActive,
                    treasureFollowerRoomRetryCooldownRemainingSeconds = DungeonFrontierService.TreasureFollowerRoomRetryCooldownRemainingSeconds,
                    treasureFollowerCofferSeekRoomIndex = DungeonFrontierService.TreasureFollowerCofferSeekRoomIndex,
                    treasureFollowerCofferSeekState = DungeonFrontierService.TreasureFollowerCofferSeekStateName,
                    treasureFollowerCofferSeekTargetKey = DungeonFrontierService.TreasureFollowerCofferSeekTargetKey,
                    treasureFollowerCofferSeekTargetName = DungeonFrontierService.TreasureFollowerCofferSeekTargetName,
                    treasureFollowerCofferSeekTargetPosition = DungeonFrontierService.TreasureFollowerCofferSeekTargetPosition is { } frontierCofferSeekPosition
                        ? BuildPositionPayload(frontierCofferSeekPosition)
                        : null,
                    treasureFollowerCofferSeekReached = DungeonFrontierService.TreasureFollowerCofferSeekReached,
                    treasureFollowerCofferSeekAttempted = DungeonFrontierService.TreasureFollowerCofferSeekAttempted,
                    treasureFollowerCofferSeekLastReason = DungeonFrontierService.TreasureFollowerCofferSeekLastReason,
                    treasureFollowerDoorFollowThroughActive = ExecutionService.TreasureFollowerDoorFollowThroughActive,
                    treasureFollowerDoorFollowThroughCandidateKey = ExecutionService.TreasureFollowerDoorFollowThroughCandidateKey,
                    treasureFollowerDoorFollowThroughCandidateName = ExecutionService.TreasureFollowerDoorFollowThroughCandidateName,
                    treasureFollowerDoorFollowThroughTarget = ExecutionService.TreasureFollowerDoorFollowThroughTarget,
                    treasureFollowerDoorFollowThroughStage = ExecutionService.TreasureFollowerDoorFollowThroughStage,
                    treasureFollowerPostTransitSettleRemainingSeconds = ExecutionService.TreasureFollowerPostTransitSettleRemainingSeconds,
                    liveTreasureDoorCandidateCount = DungeonFrontierService.LiveTreasureDoorCandidateCount,
                    currentTreasureRouteSource = DungeonFrontierService.CurrentTreasureRouteSource,
                    activeMapId = DungeonFrontierService.ActiveMapId,
                    activeMapName = DungeonFrontierService.ActiveMapName,
                    totalPoints = DungeonFrontierService.TotalPoints,
                    visitedPoints = DungeonFrontierService.VisitedPoints,
                    manualMapXzDestinationCount = DungeonFrontierService.ManualMapXzDestinationCount,
                    visitedManualMapXzDestinations = DungeonFrontierService.VisitedManualMapXzDestinations,
                    manualXyzDestinationCount = DungeonFrontierService.ManualXyzDestinationCount,
                    visitedManualXyzDestinations = DungeonFrontierService.VisitedManualXyzDestinations,
                    manualDestinationTarget = ExecutionService.CurrentManualDestinationTarget,
                    manualDestinationDistance = ExecutionService.CurrentManualDestinationDistance,
                    manualDestinationLastProgressAgeSeconds = ExecutionService.ManualDestinationLastProgressAgeSeconds,
                    manualDestinationLastGhostReason = DungeonFrontierService.LastGhostedManualDestinationReason,
                    currentTarget = DungeonFrontierService.CurrentTarget?.Name,
                    currentTargetKey = DungeonFrontierService.CurrentTarget?.Key,
                    currentTargetTreasureRouteSource = DungeonFrontierService.CurrentTarget?.TreasureRouteSource,
                    currentTargetIsLiveTreasureDoorCandidate = DungeonFrontierService.CurrentTarget?.IsLiveTreasureDoorCandidate,
                    currentTargetMapId = DungeonFrontierService.CurrentTarget?.MapId,
                    currentTargetTreasureRoomIndex = DungeonFrontierService.CurrentTarget?.TreasureRoomIndex,
                    currentTargetTreasurePassageGroup = DungeonFrontierService.CurrentTarget?.TreasurePassageGroup,
                    currentTargetPosition = DungeonFrontierService.CurrentTarget is { } frontierPoint
                        ? BuildPositionPayload(frontierPoint.Position)
                        : null,
                    currentTargetMapCoordinates = DungeonFrontierService.CurrentTarget?.MapCoordinates is { } mapCoordinates
                        ? new { x = MathF.Round(mapCoordinates.X, 2), z = MathF.Round(mapCoordinates.Y, 2) }
                        : null,
                    currentTargetWorldCoordinates = DungeonFrontierService.CurrentTarget is { IsManualXyzDestination: true } xyzFrontierPoint
                        ? BuildPositionPayload(xyzFrontierPoint.Position)
                        : null,
                    scoutHeading = DungeonFrontierService.CurrentHeading.HasValue
                        ? BuildPositionPayload(DungeonFrontierService.CurrentHeading.Value)
                        : null,
                },
                observations = new
                {
                    liveMonsters = ObservationMemoryService.Current.LiveMonsters.Select(x => new { x.Name, x.DataId, x.MapId, Position = BuildPositionPayload(x.Position) }),
                    liveFollowTargets = ObservationMemoryService.Current.LiveFollowTargets.Select(x => new { x.Name, x.DataId, x.MapId, Position = BuildPositionPayload(x.Position) }),
                    monsterGhosts = ObservationMemoryService.Current.MonsterGhosts.Select(x => new { x.Name, x.DataId, x.MapId, Position = BuildPositionPayload(x.Position) }),
                    liveInteractables = ObservationMemoryService.Current.LiveInteractables.Select(x => new { x.Name, x.DataId, x.MapId, Position = BuildPositionPayload(x.Position), classification = x.Classification.ToString() }),
                    interactableGhosts = ObservationMemoryService.Current.InteractableGhosts.Select(x => new { x.Name, x.DataId, x.MapId, Position = BuildPositionPayload(x.Position), classification = x.Classification.ToString(), ghostReason = x.GhostReason.ToString() }),
                },
            },
            JsonOptions);

    public string GetHigherLowerLiveProbeJson()
        => JsonSerializer.Serialize(
            new
            {
                liveProbe = TreasureHighLowDiagnosticService.CaptureLiveProbe(),
                vfxDataminingEnabled = Configuration.HigherLowerVfxDataminingEnabled,
                automation = HigherLowerAutomationService.CaptureDebugState(),
            },
            JsonOptions);

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"[ADS] Failed to open URL: {url}");
        }
    }

    public void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"[ADS] Failed to open path: {path}");
        }
    }

    public void PrintStatus(string message)
        => ChatGui.Print($"[ADS] {message}");

    private static string NormalizeRepairMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "self" or "selfrepair" or "self-repair" => "self",
            "npc" or "npcrepair" or "npc-repair" => "npc",
            "npc-no-inn" or "npcnoinn" or "noinn" or "no-inn" => "npc-no-inn",
            "npc-no-teleport-no-inn" or "npc-no-tp-no-inn" or "npc-no-inn-no-tp" or "npcrepair-no-teleport-no-inn" => "npc-no-teleport-no-inn",
            _ => string.Empty,
        };
    }

    private static object BuildPositionPayload(System.Numerics.Vector3 value)
        => new
        {
            x = MathF.Round(value.X, 2),
            y = MathF.Round(value.Y, 2),
            z = MathF.Round(value.Z, 2),
        };

    private static string FormatOptionalId(ulong? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private void QueueDutyOwnershipRemoteUpdate()
        => RemoteJsonUpdateService.TryStartStaleUpdate("duty ownership");

    private void ApplyTreasureDungeonRole(
        TreasureDungeonRoleInference inference,
        string reason,
        bool resetFollowerProgressForOwnership = false)
    {
        ExecutionService.SetTreasureDungeonRole(inference);
        DungeonFrontierService.SetTreasureDungeonRole(inference, resetFollowerProgressForOwnership);
        Log.Information(
            $"[ADS] Treasure role {reason}: display={inference.DisplayName}, behavior={inference.Role}, source={inference.Source}, character='{inference.CharacterKey}'. {inference.Detail}");
    }

    private void InferAndApplyTreasureDungeonRole(string reason, bool resetFollowerProgressForOwnership = false)
    {
        var inference = TreasureDungeonRoleDetector.Infer();
        ApplyTreasureDungeonRole(inference, $"inference for {reason}", resetFollowerProgressForOwnership);
    }

    private void EnsureTreasureDungeonRoleInferredForOwnedDuty()
    {
        var context = DutyContextService.Current;
        var ownershipMode = ExecutionService.CurrentMode;
        if (ownershipMode is not (OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside)
            || !context.PluginEnabled
            || !context.IsLoggedIn
            || !context.InInstancedDuty)
        {
            if (ownershipMode is not (OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside))
                ResetOwnedTreasureRoleInferenceLatch();
            return;
        }

        var dutyKey = GetDutyKey(context);
        if (dutyKey == 0
            || (lastOwnedTreasureRoleInferenceDutyKey == dutyKey
                && lastOwnedTreasureRoleInferenceMode == ownershipMode))
        {
            return;
        }

        InferAndApplyTreasureDungeonRole(
            $"{ownershipMode} first owned duty tick",
            resetFollowerProgressForOwnership: ownershipMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside);
        RememberOwnedTreasureRoleInference(context, ownershipMode);
    }

    private void RememberOwnedTreasureRoleInference(DutyContextSnapshot context, OwnershipMode ownershipMode)
    {
        lastOwnedTreasureRoleInferenceDutyKey = GetDutyKey(context);
        lastOwnedTreasureRoleInferenceMode = ownershipMode;
    }

    private void ResetOwnedTreasureRoleInferenceLatch()
    {
        lastOwnedTreasureRoleInferenceDutyKey = 0;
        lastOwnedTreasureRoleInferenceMode = OwnershipMode.Idle;
    }

    private static uint GetDutyKey(DutyContextSnapshot context)
        => context.TerritoryTypeId != 0
            ? context.TerritoryTypeId
            : context.ContentFinderConditionId;

    private static string BuildTreasureDutyRecoveryKey(DutyContextSnapshot context)
        => $"{context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}:{context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsSupportedTreasureDutyContext(DutyContextSnapshot context)
        => context.InInstancedDuty
           && (context.CurrentDuty?.Category == DutyCategory.TreasureDungeon
               || TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId));

    private bool ShouldRunHigherLowerHeavyWork(DutyContextSnapshot context)
    {
        if (!IsSupportedTreasureDutyContext(context))
            return false;

        var runtime = TreasureHighLowDiagnosticService.CaptureRuntimeState();
        if (runtime.Active)
            return true;

        var lastSignalUtc = TreasureHighLowDiagnosticService.LastHigherLowerSignalUtc;
        return lastSignalUtc != DateTime.MinValue
               && DateTime.UtcNow - lastSignalUtc <= HigherLowerRecentSignalWindow;
    }

    private bool IsActiveTreasureDutyOwnershipMode()
        => ExecutionService.CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside;

    private bool HasTreasureDutyRecoveryMarker()
        => !string.IsNullOrWhiteSpace(Configuration.TreasureDutyRecoveryKey);

    private bool IsTreasureDutyRecoveryStale()
    {
        if (Configuration.TreasureDutyRecoveryUtc == DateTime.MinValue)
            return true;

        var age = DateTime.UtcNow - Configuration.TreasureDutyRecoveryUtc;
        return age > TreasureDutyRecoveryTtl || age < -TimeSpan.FromMinutes(5);
    }

    private bool TreasureDutyRecoveryMatchesCurrentContext(DutyContextSnapshot context)
    {
        var currentKey = BuildTreasureDutyRecoveryKey(context);
        if (string.Equals(Configuration.TreasureDutyRecoveryKey, currentKey, StringComparison.Ordinal))
            return true;

        var parts = Configuration.TreasureDutyRecoveryKey.Split(':', 2);
        if (parts.Length != 2
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var territoryTypeId)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var contentFinderConditionId))
        {
            return false;
        }

        return territoryTypeId == context.TerritoryTypeId
               && (contentFinderConditionId == 0
                   || context.ContentFinderConditionId == 0
                   || contentFinderConditionId == context.ContentFinderConditionId);
    }

    private void WriteTreasureDutyRecoveryMarker(DutyContextSnapshot context, string reason, bool force = false)
    {
        if (!IsActiveTreasureDutyOwnershipMode()
            || !context.PluginEnabled
            || !context.IsLoggedIn
            || !IsSupportedTreasureDutyContext(context))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var key = BuildTreasureDutyRecoveryKey(context);
        var role = ExecutionService.TreasureDungeonRole.ToString();
        var keyChanged = !string.Equals(Configuration.TreasureDutyRecoveryKey, key, StringComparison.Ordinal);
        var roleChanged = !string.Equals(Configuration.TreasureDutyRecoveryRole, role, StringComparison.Ordinal);
        var shouldRefreshTime = Configuration.TreasureDutyRecoveryUtc == DateTime.MinValue
                                || now - Configuration.TreasureDutyRecoveryUtc >= TreasureDutyRecoveryRefreshInterval;
        if (!force && !keyChanged && !roleChanged && !shouldRefreshTime)
            return;

        Configuration.TreasureDutyRecoveryKey = key;
        Configuration.TreasureDutyRecoveryUtc = now;
        Configuration.TreasureDutyRecoveryRole = role;
        Configuration.Save();

        if (force || keyChanged || roleChanged)
        {
            Log.Information(
                $"[ADS] Wrote treasure duty recovery marker after {reason}: key={key}, role={role}.");
        }
    }

    private void ClearTreasureDutyRecoveryMarker(string reason)
    {
        if (string.IsNullOrWhiteSpace(Configuration.TreasureDutyRecoveryKey)
            && Configuration.TreasureDutyRecoveryUtc == DateTime.MinValue
            && string.IsNullOrWhiteSpace(Configuration.TreasureDutyRecoveryRole))
        {
            return;
        }

        var previousKey = Configuration.TreasureDutyRecoveryKey;
        Configuration.TreasureDutyRecoveryKey = string.Empty;
        Configuration.TreasureDutyRecoveryUtc = DateTime.MinValue;
        Configuration.TreasureDutyRecoveryRole = string.Empty;
        Configuration.Save();
        Log.Information($"[ADS] Cleared treasure duty recovery marker after {reason}. Previous key={previousKey}.");
    }

    private void TryRecoverTreasureDutyOwnership()
    {
        if (treasureDutyRecoveryAttemptedThisLoad || !HasTreasureDutyRecoveryMarker())
            return;

        var context = DutyContextService.Current;
        if (!context.PluginEnabled || !context.IsLoggedIn || context.IsUnsafeTransition)
            return;

        if (IsTreasureDutyRecoveryStale())
        {
            treasureDutyRecoveryAttemptedThisLoad = true;
            ClearTreasureDutyRecoveryMarker("stale reload recovery marker");
            return;
        }

        if (!context.InInstancedDuty)
        {
            treasureDutyRecoveryAttemptedThisLoad = true;
            ClearTreasureDutyRecoveryMarker("outside-duty reload cleanup");
            return;
        }

        if (!IsSupportedTreasureDutyContext(context))
        {
            treasureDutyRecoveryAttemptedThisLoad = true;
            ClearTreasureDutyRecoveryMarker("unsupported-duty reload recovery");
            return;
        }

        if (!TreasureDutyRecoveryMatchesCurrentContext(context))
        {
            treasureDutyRecoveryAttemptedThisLoad = true;
            ClearTreasureDutyRecoveryMarker("different-duty reload recovery");
            return;
        }

        treasureDutyRecoveryAttemptedThisLoad = true;
        if (IsActiveTreasureDutyOwnershipMode())
        {
            WriteTreasureDutyRecoveryMarker(context, "already-owned reload recovery", force: true);
            return;
        }

        Log.Information(
            $"[ADS] Recovering owned treasure duty from marker key={Configuration.TreasureDutyRecoveryKey}, storedRole={Configuration.TreasureDutyRecoveryRole}.");
        if (!ResumeDutyFromInside())
            ClearTreasureDutyRecoveryMarker("failed reload recovery");
    }

    private void CleanupTreasureDutyRuntimeOutsideDuty()
    {
        if (DutyContextService.Current.InInstancedDuty)
            return;

        ClearTreasureDutyRecoveryMarker("outside duty");
    }

    private bool ShouldUseTreasureFollowerBmraiFollow()
    {
        var context = DutyContextService.Current;
        if (!IsActiveTreasureDutyOwnershipMode()
            || !context.PluginEnabled
            || !context.IsLoggedIn)
        {
            return false;
        }

        if (ExecutionService.TreasureDungeonRole == TreasureDungeonRole.Follower)
            return true;

        return !context.InInstancedDuty
               && ExecutionService.TreasureDungeonRoleAllowsOutsideBmraiFollow;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var updateStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var slowestSection = "none";
        var slowestMs = 0d;

        void Measure(string section, Action action)
        {
            var sectionStopwatch = System.Diagnostics.Stopwatch.StartNew();
            action();
            sectionStopwatch.Stop();
            var elapsedMs = sectionStopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMs > slowestMs)
            {
                slowestMs = elapsedMs;
                slowestSection = section;
            }
        }

        try
        {
            TreasureHighLowDiagnosticService.BeginFrameworkTick();
            Measure("duty-context", () => DutyContextService.Update(Configuration.PluginEnabled));
            Measure("debug-strafe", () => DebugStrafeService.Update(DutyContextService.Current.IsLoggedIn, Configuration.PluginEnabled));
            Measure("remote-json-complete", QueueCompletedRemoteJsonReload);
            Measure("dialog", () => DialogAutomationService.Update(
                DutyContextService.Current,
                ExecutionService.CurrentMode,
                Configuration.PluginEnabled,
                Configuration.ProcessDialogRulesOutsideOwnedDuty,
                UtilityAutomationService.SuppressesGenericYesNo));
            Measure("bmr-reflection", BmrReflectionService.Update);
            Measure("duty-housekeeping", () =>
            {
                CleanupTreasureDutyRuntimeOutsideDuty();
                TryRecoverTreasureDutyOwnership();
                EnsureTreasureDungeonRoleInferredForOwnedDuty();
                WriteTreasureDutyRecoveryMarker(DutyContextService.Current, "owned treasure duty tick");
            });

            if (DutyContextService.Current.IsUnsafeTransition)
            {
                Measure("transition-hold", () =>
                {
                    ObservationMemoryService.HoldUnsafeTransition();
                    DungeonFrontierService.HoldUnsafeTransition(DutyContextService.Current);
                    ObjectivePlannerService.Update(
                        DutyContextService.Current,
                        ObservationSnapshot.Empty,
                        ExecutionService.CurrentMode,
                        Configuration.ConsiderTreasureCoffers);
                    ExecutionService.Update(
                        DutyContextService.Current,
                        ObjectivePlannerService.Current,
                        ObservationSnapshot.Empty,
                        Configuration.PluginEnabled,
                        Configuration.ConsiderTreasureCoffers,
                        DialogAutomationService.DialogStatus);
                    TreasureHighLowDiagnosticService.Update(
                        DutyContextService.Current,
                        ObservationSnapshot.Empty,
                        ObjectivePlannerService.Current,
                        DialogAutomationService.DialogStatus);
                    TreasureFollowerAutoMoveAssistService.Update(
                        DutyContextService.Current,
                        ExecutionService.TreasureDungeonRole,
                        BossModMultiboxFollowService.FollowerMovementOwnedByBmrai,
                        TreasurePortalOpenerTracker.CurrentOrRecentDirect);
                    TreasureFollowerDutyExitMonitorService.Update(
                        DutyContextService.Current,
                        IsSupportedTreasureDutyContext(DutyContextService.Current),
                        ExecutionService.TreasureDungeonRole,
                        ExecutionService.TreasureDungeonRoleDisplayName);
                    UpdateDtrBar();
                });
                return;
            }

            Measure("json-reload", UpdateJsonReloads);

            var shouldUseTreasureFollowerBmraiFollow = false;
            Measure("treasure-follow-mode", () => shouldUseTreasureFollowerBmraiFollow = ShouldUseTreasureFollowerBmraiFollow());
            Measure("higher-lower-server", () => HigherLowerServerEventTraceService.Update(DutyContextService.Current));
            var allowHigherLowerHeavyWork = false;
            Measure("higher-lower-gate", () => allowHigherLowerHeavyWork = ShouldRunHigherLowerHeavyWork(DutyContextService.Current));
            var treasureInteractionWitness = HigherLowerServerEventTraceService.LastTreasureInteractionWitness;
            Measure("treasure-witness", () =>
            {
                DungeonFrontierService.RecordTreasureInteractionWitness(treasureInteractionWitness);
                var directWitnessOpener = TreasurePortalOpenerTracker.Update(DutyContextService.Current, shouldUseTreasureFollowerBmraiFollow, treasureInteractionWitness);
                if (directWitnessOpener is not null)
                    BossModMultiboxFollowService.ApplyDirectTreasurePortalOpener(
                        directWitnessOpener,
                        DutyContextService.Current,
                        "interaction witness");
                var followOpener = TreasurePortalOpenerTracker.CurrentOrRecentDirect;
                if (followOpener is not null)
                {
                    BossModMultiboxFollowService.ReapplyDirectTreasurePortalOpenerIfNeeded(
                        followOpener,
                        DutyContextService.Current,
                        "stable follower duty truth");
                }

                BossModMultiboxFollowService.Update(
                    ExecutionService.TreasureDungeonRole,
                    ExecutionService.TreasureDungeonRoleDisplayName,
                    followOpener,
                    shouldUseTreasureFollowerBmraiFollow);
                TreasureFollowerAutoMoveAssistService.Update(
                    DutyContextService.Current,
                    ExecutionService.TreasureDungeonRole,
                    BossModMultiboxFollowService.FollowerMovementOwnedByBmrai,
                    followOpener);
                ExecutionService.SetTreasureFollowerBmraiMovementAuthority(
                    BossModMultiboxFollowService.FollowerMovementOwnedByBmrai,
                    BossModMultiboxFollowService.FollowerMovementStatus);
            });
            Measure("td-exit-monitor", () => TreasureFollowerDutyExitMonitorService.Update(
                DutyContextService.Current,
                IsSupportedTreasureDutyContext(DutyContextService.Current),
                ExecutionService.TreasureDungeonRole,
                ExecutionService.TreasureDungeonRoleDisplayName));
            Measure("higher-lower-vfx", () => HigherLowerVfxTraceService.Update(DutyContextService.Current, allowHigherLowerHeavyWork));
            Measure("higher-lower-card", () => HigherLowerCardVfxSolverService.Update(DutyContextService.Current, allowHigherLowerHeavyWork));
            Measure("higher-lower-auto", () =>
            {
                HigherLowerAutomationService.Update(DutyContextService.Current, ExecutionService.CurrentMode, Configuration.PluginEnabled);
                ExecutionService.SetHigherLowerAutomationHold(
                    HigherLowerAutomationService.HoldMovement,
                    HigherLowerAutomationService.Status,
                    HigherLowerAutomationService.BlocksDutyExit,
                    HigherLowerAutomationService.LastHigherLowerActivityUtc);
            });
            Measure("loot", () => LootAutomationService.Update(DutyContextService.Current, ExecutionService.CurrentMode, Configuration.PluginEnabled));
            Measure("observation", () => ObservationMemoryService.Update(DutyContextService.Current, Configuration.ConsiderTreasureCoffers));
            Measure("frontier", () => DungeonFrontierService.Update(DutyContextService.Current, ObservationMemoryService.Current));
            Measure("planner", () => ObjectivePlannerService.Update(
                DutyContextService.Current,
                ObservationMemoryService.Current,
                ExecutionService.CurrentMode,
                Configuration.ConsiderTreasureCoffers));
            Measure("execution", () => ExecutionService.Update(
                DutyContextService.Current,
                ObjectivePlannerService.Current,
                ObservationMemoryService.Current,
                Configuration.PluginEnabled,
                Configuration.ConsiderTreasureCoffers,
                DialogAutomationService.DialogStatus));
            Measure("diagnostics", () => TreasureHighLowDiagnosticService.Update(
                DutyContextService.Current,
                ObservationMemoryService.Current,
                ObjectivePlannerService.Current,
                DialogAutomationService.DialogStatus));
            Measure("inn", InnEntryService.Update);
            Measure("utility", UtilityAutomationService.Update);
            Measure("dtr", UpdateDtrBar);
        }
        finally
        {
            updateStopwatch.Stop();
            ReportFrameworkSlowUpdate(updateStopwatch.Elapsed.TotalMilliseconds, slowestSection, slowestMs);
        }
    }

    private void QueueCompletedRemoteJsonReload()
    {
        if (!RemoteJsonUpdateService.TryConsumeCompletedUpdate())
            return;

        pendingRemoteJsonReloadSteps.Enqueue(RemoteJsonReloadStep.ObjectRules);
        pendingRemoteJsonReloadSteps.Enqueue(RemoteJsonReloadStep.DialogRules);
        pendingRemoteJsonReloadSteps.Enqueue(RemoteJsonReloadStep.DutyMaturity);
        pendingRemoteJsonReloadSteps.Enqueue(RemoteJsonReloadStep.TreasureRoutes);
        Log.Information("[ADS] Remote config update completed; queued cache reload across framework frames.");
    }

    private void UpdateJsonReloads()
    {
        if (ShouldDeferJsonReloads(out var reason))
        {
            if (pendingRemoteJsonReloadSteps.Count > 0 && DateTime.UtcNow >= nextRemoteJsonReloadDeferredLogUtc)
            {
                nextRemoteJsonReloadDeferredLogUtc = DateTime.UtcNow.AddSeconds(5);
                Log.Debug($"[ADS] Deferring {pendingRemoteJsonReloadSteps.Count} remote config reload step(s): {reason}.");
            }

            return;
        }

        nextRemoteJsonReloadDeferredLogUtc = DateTime.MinValue;
        if (pendingRemoteJsonReloadSteps.TryDequeue(out var step))
        {
            RunRemoteJsonReloadStep(step);
            return;
        }

        ObjectPriorityRuleService.ReloadIfChanged();
        DialogYesNoRuleService.ReloadIfChanged();
        TreasureDungeonData.ReloadIfChanged();
    }

    private bool ShouldDeferJsonReloads(out string reason)
    {
        var context = DutyContextService.Current;
        if (context.BetweenAreas)
        {
            reason = "BetweenAreas active";
            return true;
        }

        if (context.BetweenAreas51)
        {
            reason = "BetweenAreas51 active";
            return true;
        }

        if (DialogAutomationService.DialogVisible)
        {
            reason = "SelectYesno visible";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private void RunRemoteJsonReloadStep(RemoteJsonReloadStep step)
    {
        switch (step)
        {
            case RemoteJsonReloadStep.ObjectRules:
                ObjectPriorityRuleService.Reload();
                break;
            case RemoteJsonReloadStep.DialogRules:
                DialogYesNoRuleService.Reload();
                break;
            case RemoteJsonReloadStep.DutyMaturity:
                DutyCatalogService.ReloadMaturity();
                break;
            case RemoteJsonReloadStep.TreasureRoutes:
                TreasureDungeonData.Reload();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown remote JSON reload step.");
        }
    }

    private void ReportFrameworkSlowUpdate(double elapsedMs, string slowestSection, double slowestMs)
    {
        if (elapsedMs < FrameworkSlowLogThresholdMs)
            return;

        var now = DateTime.UtcNow;
        var context = DutyContextService.Current;
        var slowContext = new FrameworkSlowUpdateContext(
            context.TerritoryTypeId,
            context.MapId,
            context.BetweenAreas,
            context.BetweenAreas51,
            DialogAutomationService.DialogVisible,
            DialogAutomationService.DialogRule,
            DialogAutomationService.DialogStatus,
            HigherLowerVfxTraceService.PendingCount,
            HigherLowerVfxTraceService.LastTrackedSnapshotCount);
        lastFrameworkSlowUpdateMs = elapsedMs;
        lastFrameworkSlowUpdateSection = slowestSection;
        lastFrameworkSlowUpdateUtc = now;
        lastFrameworkSlowUpdateContext = slowContext;

        if (now < nextFrameworkSlowLogUtc)
            return;

        nextFrameworkSlowLogUtc = now + FrameworkSlowLogCooldown;
        Log.Warning(
            "[ADS][HITCH] framework update slow elapsedMs={ElapsedMs:0.0}; slowSection={SlowSection}; slowSectionMs={SlowSectionMs:0.0}; territory={Territory}; map={Map}; betweenAreas={BetweenAreas}; betweenAreas51={BetweenAreas51}; dialogVisible={DialogVisible}; dialogRule={DialogRule}; dialogStatus={DialogStatus}; pendingHigherLowerVfx={PendingHigherLowerVfx}; trackedHigherLowerVfx={TrackedHigherLowerVfx}.",
            elapsedMs,
            slowestSection,
            slowestMs,
            slowContext.territoryTypeId,
            slowContext.mapId,
            slowContext.betweenAreas,
            slowContext.betweenAreas51,
            slowContext.dialogVisible,
            slowContext.dialogRule,
            slowContext.dialogStatus,
            slowContext.pendingHigherLowerVfxCount,
            slowContext.trackedHigherLowerVfxCount);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        if (TreasurePortalOpenerTracker.HandleChatMessage(text)
            && TreasurePortalOpenerTracker.Current is { } portalChatOpener)
        {
            BossModMultiboxFollowService.ApplyDirectTreasurePortalOpener(
                portalChatOpener,
                DutyContextService.Current,
                "portal chat");
        }

        HigherLowerAutomationService.HandleChatMessage(text);
        ExecutionService.HandleChatMessage(text);
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(uint territoryId)
    {
        var context = DutyContextService.Current;
        var dutyName = context.CurrentDuty?.EnglishName ?? $"territory {territoryId}";
        ClearTreasureDutyRecoveryMarker("duty completion");
        TreasurePortalOpenerTracker.ClearPendingOpener("duty completion");
        TreasurePortalOpenerRelayService.Clear("duty completion");
        BossModMultiboxFollowService.Clear("duty completion");
        if (!ExecutionService.IsOwned)
        {
            ObservationMemoryService.Reset();
            DungeonFrontierService.Reset();
            Log.Information($"[ADS] DutyCompleted event for {dutyName}; observation memory cleared while ADS was not executing.");
            return;
        }

        if (ShouldRunDutyCompletionTreasureSweep(context)
            && ExecutionService.BeginDutyCompletionTreasureSweep(context, dutyName))
        {
            PrintStatus(ExecutionService.LastStatus);
            UpdateDtrBar();
            Log.Information($"[ADS] DutyCompleted event for {dutyName}; ADS kept ownership for the final treasure sweep.");
            return;
        }

        ObservationMemoryService.Reset();
        DungeonFrontierService.Reset();
        ExecutionService.CompleteDuty(dutyName);
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        Log.Information($"[ADS] DutyCompleted event for {dutyName}; ownership released and observation memory cleared.");
    }

    private bool ShouldRunDutyCompletionTreasureSweep(DutyContextSnapshot context)
        => Configuration.ConsiderTreasureCoffers
           && context.InInstancedDuty
           && (context.CurrentDuty?.Category == DutyCategory.TreasureDungeon
               || TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId));

    private void RegisterCommands()
    {
        var info = new CommandInfo(OnCommand)
        {
            HelpMessage =
                "/ads - toggle the main window\n" +
                "/ads config - open settings\n" +
                "/ads obj - toggle the object explorer\n" +
                "/ads ghosts - toggle the ghost inspector\n" +
                "/ads labels - toggle the frontier label window\n" +
                "/ads mini - toggle the compact control window\n" +
                "/ads loot - toggle the loot control window\n" +
                "/ads debug on|off|status|release - toggle mini-window debug strafe controls\n" +
                "/ads rules - toggle the rules editor\n" +
                "/ads dialogs - toggle the dialog rules editor\n" +
                "/ads hl - toggle the Higher/Lower calibration window\n" +
                "/ads treasure - open treasure route editor\n" +
                "/ads events - toggle the server event explorer\n" +
                "/ads vfx - toggle the VFX explorer\n" +
                "/ads reflection - toggle BMR reflection controls\n" +
                "/ads mapeffects - alias for /ads events\n" +
                "/ads ws - reset windows to 1,1\n" +
                "/ads j - jump windows to visible random positions\n" +
                "/ads outside - queue outside ownership\n" +
                "/ads inside - claim ownership inside duty\n" +
                "/ads resume - resume inside duty\n" +
                "/ads leave - request leave state - if chests nearby it will grab them then wait 10 seconds\n" +
                "/ads enterinn - move to a nearby innkeeper and enter the inn\n" +
                "/ads repair self|npc|npc-no-inn|npc-no-teleport-no-inn - start reusable repair automation\n" +
                "/ads selfrepair - open self-repair and repair equipped gear\n" +
                "/ads npcrepair - move to a nearby repair NPC and repair equipped gear\n" +
                "/ads npcrepair noinn - NPC repair without inn fallback\n" +
                "/ads npcrepair-no-teleport-no-inn - NPC repair only if a mender is within 120y\n" +
                "/ads extractmateria - extract ready materia from gear\n" +
                "/ads desynthfrominventory - desynth inventory-only items\n" +
                "/ads lootoff|lootneed|lootgreed|lootpass - set loot rolling mode\n" +
                "/ads lootregon|lootregoff - toggle Need missing registrables\n" +
                "/ads td-monitor-on|td-monitor-off - arm/disarm treasure follower exit cleanup monitor\n" +
                "/ads hldebug on|off|dump|state|trace [seconds]|export|exportpath <tex> [u v w h]|card <1-9> [current|next|previous]|board <left> <right> [label...]|solver|status|folder - Higher/Lower diagnostic file logging\n" +
                "/ads hlauto on|off|status - Higher/Lower guarded automation\n" +
                "/ads stop - drop ownership",
            ShowInHelp = true,
        };

        CommandManager.AddHandler(PluginInfo.Command, info);
        CommandManager.AddHandler(PluginInfo.AliasCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /ads." });
        CommandManager.AddHandler(PluginInfo.SecondaryAliasCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /ads." });
    }

    private void UnregisterCommands()
    {
        CommandManager.RemoveHandler(PluginInfo.Command);
        CommandManager.RemoveHandler(PluginInfo.AliasCommand);
        CommandManager.RemoveHandler(PluginInfo.SecondaryAliasCommand);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = (args ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ToggleMainUi();
            return;
        }

        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfigUi();
            return;
        }

        if (trimmed.Equals("obj", StringComparison.OrdinalIgnoreCase))
        {
            ToggleObjectExplorerUi();
            return;
        }

        if (trimmed.Equals("ghosts", StringComparison.OrdinalIgnoreCase))
        {
            ToggleGhostListUi();
            return;
        }

        if (trimmed.Equals("labels", StringComparison.OrdinalIgnoreCase))
        {
            ToggleFrontierLabelUi();
            return;
        }

        if (trimmed.Equals("mini", StringComparison.OrdinalIgnoreCase))
        {
            ToggleQuickControlUi();
            return;
        }

        if (trimmed.Equals("debug", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("debug ", StringComparison.OrdinalIgnoreCase))
        {
            HandleDebugCommand(trimmed);
            return;
        }

        if (trimmed.Equals("loot", StringComparison.OrdinalIgnoreCase))
        {
            ToggleLootUi();
            return;
        }

        if (trimmed.Equals("lootoff", StringComparison.OrdinalIgnoreCase))
        {
            SetLootMode(LootRollMode.Off);
            return;
        }

        if (trimmed.Equals("lootneed", StringComparison.OrdinalIgnoreCase))
        {
            SetLootMode(LootRollMode.Need);
            return;
        }

        if (trimmed.Equals("lootgreed", StringComparison.OrdinalIgnoreCase))
        {
            SetLootMode(LootRollMode.Greed);
            return;
        }

        if (trimmed.Equals("lootpass", StringComparison.OrdinalIgnoreCase))
        {
            SetLootMode(LootRollMode.Pass);
            return;
        }

        if (trimmed.Equals("lootregon", StringComparison.OrdinalIgnoreCase))
        {
            SetLootRegistrableNeedingEnabled(true, printStatus: true);
            return;
        }

        if (trimmed.Equals("lootregoff", StringComparison.OrdinalIgnoreCase))
        {
            SetLootRegistrableNeedingEnabled(false, printStatus: true);
            return;
        }

        if (trimmed.Equals("td-monitor-on", StringComparison.OrdinalIgnoreCase))
        {
            TreasureFollowerDutyExitMonitorService.Arm(DutyContextService.Current, "manual command");
            PrintStatus(TreasureFollowerDutyExitMonitorService.Status);
            return;
        }

        if (trimmed.Equals("td-monitor-off", StringComparison.OrdinalIgnoreCase))
        {
            TreasureFollowerDutyExitMonitorService.Disarm("manual command");
            PrintStatus(TreasureFollowerDutyExitMonitorService.Status);
            return;
        }

        if (trimmed.Equals("rules", StringComparison.OrdinalIgnoreCase))
        {
            ToggleRuleEditorUi();
            return;
        }

        if (trimmed.Equals("dialogs", StringComparison.OrdinalIgnoreCase))
        {
            ToggleDialogRuleEditorUi();
            return;
        }

        if (trimmed.Equals("hl", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("higherlower", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("higher-lower", StringComparison.OrdinalIgnoreCase))
        {
            ToggleHigherLowerUi();
            return;
        }

        if (trimmed.Equals("treasure", StringComparison.OrdinalIgnoreCase))
        {
            OpenTreasureRouteEditorUi();
            return;
        }

        if (trimmed.Equals("events", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("mapeffects", StringComparison.OrdinalIgnoreCase))
        {
            ToggleServerEventExplorerUi();
            return;
        }

        if (trimmed.Equals("vfx", StringComparison.OrdinalIgnoreCase))
        {
            ToggleVfxExplorerUi();
            return;
        }

        if (trimmed.Equals("reflection", StringComparison.OrdinalIgnoreCase))
        {
            ToggleReflectionUi();
            return;
        }

        if (trimmed.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            ResetWindowPositions();
            return;
        }

        if (trimmed.Equals("j", StringComparison.OrdinalIgnoreCase))
        {
            JumpWindows();
            return;
        }

        if (trimmed.Equals("outside", StringComparison.OrdinalIgnoreCase))
        {
            StartDutyFromOutside();
            return;
        }

        if (trimmed.Equals("inside", StringComparison.OrdinalIgnoreCase))
        {
            StartDutyFromInside();
            return;
        }

        if (trimmed.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            ResumeDutyFromInside();
            return;
        }

        if (trimmed.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            LeaveDuty();
            return;
        }

        if (trimmed.Equals("enterinn", StringComparison.OrdinalIgnoreCase))
        {
            StartInnEntry();
            return;
        }

        if (trimmed.Equals("repair", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus("Repair mode must be self, npc, npc-no-inn, or npc-no-teleport-no-inn.");
            return;
        }

        if (trimmed.StartsWith("repair ", StringComparison.OrdinalIgnoreCase))
        {
            StartRepair(trimmed["repair ".Length..]);
            return;
        }

        if (trimmed.Equals("selfrepair", StringComparison.OrdinalIgnoreCase))
        {
            StartSelfRepair();
            return;
        }

        if (trimmed.Equals("npcrepair noinn", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("npcrepair no-inn", StringComparison.OrdinalIgnoreCase))
        {
            StartNpcRepairNoInn();
            return;
        }

        if (trimmed.Equals("npcrepair-no-teleport-no-inn", StringComparison.OrdinalIgnoreCase))
        {
            StartNpcRepairNoTeleportNoInn();
            return;
        }

        if (trimmed.Equals("npcrepair", StringComparison.OrdinalIgnoreCase))
        {
            StartNpcRepair();
            return;
        }

        if (trimmed.Equals("extractmateria", StringComparison.OrdinalIgnoreCase))
        {
            StartExtractMateria();
            return;
        }

        if (trimmed.Equals("desynthfrominventory", StringComparison.OrdinalIgnoreCase))
        {
            StartDesynthFromInventory();
            return;
        }

        if (trimmed.Equals("hldebug trace", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("hldebug trace ", StringComparison.OrdinalIgnoreCase))
        {
            var traceText = trimmed.Length == "hldebug trace".Length
                ? string.Empty
                : trimmed["hldebug trace ".Length..].Trim();
            var seconds = TreasureHighLowDiagnosticService.DefaultTraceSeconds;
            if (!string.IsNullOrWhiteSpace(traceText)
                && (!double.TryParse(traceText, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                    || seconds <= 0))
            {
                PrintStatus($"Higher/Lower trace must be: /ads hldebug trace [seconds], max {TreasureHighLowDiagnosticService.MaxTraceSeconds.ToString("0.###", CultureInfo.InvariantCulture)}.");
                return;
            }

            var result = TreasureHighLowDiagnosticService.StartTrace(seconds);
            PrintStatus(result.Message);
            return;
        }

        if (trimmed.Equals("hldebug export", StringComparison.OrdinalIgnoreCase))
        {
            var result = TreasureHighLowDiagnosticService.ExportCurrentTextureProbe();
            PrintStatus(result.Message);
            return;
        }

        if (trimmed.StartsWith("hldebug exportpath ", StringComparison.OrdinalIgnoreCase))
        {
            var exportArgs = trimmed["hldebug exportpath ".Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (exportArgs.Length is not (1 or 5))
            {
                PrintStatus("Higher/Lower exportpath must be: /ads hldebug exportpath <tex path> [u v w h].");
                return;
            }

            var result = TreasureHighLowDiagnosticService.ExportTexturePath(exportArgs[0], exportArgs.Skip(1).ToArray());
            PrintStatus(result.Message);
            return;
        }

        if (trimmed.Equals("hldebug", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("hldebug dump", StringComparison.OrdinalIgnoreCase))
        {
            TreasureHighLowDiagnosticService.ForceDump();
            PrintStatus("Higher/Lower diagnostic snapshot queued.");
            return;
        }

        if (trimmed.Equals("hldebug state", StringComparison.OrdinalIgnoreCase))
        {
            TreasureHighLowDiagnosticService.ForceStateProbe();
            PrintStatus("Higher/Lower focused state probe queued.");
            return;
        }

        if (trimmed.Equals("hldebug status", StringComparison.OrdinalIgnoreCase))
        {
            var path = TreasureHighLowDiagnosticService.CurrentLogPath;
            PrintStatus(
                $"Higher/Lower diagnostics enabled={TreasureHighLowDiagnosticService.Enabled}; " +
                $"vfxDatamine={TreasureHighLowDiagnosticService.VfxDataminingEnabled}; " +
                $"datamineSession={(string.IsNullOrWhiteSpace(TreasureHighLowDiagnosticService.CurrentDatamineSessionDirectory) ? "(not opened yet)" : TreasureHighLowDiagnosticService.CurrentDatamineSessionDirectory)}; " +
                $"file={(string.IsNullOrWhiteSpace(path) ? "(not opened yet)" : path)}");
            return;
        }

        if (trimmed.Equals("hldebug solver", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(HigherLowerCardVfxSolverService.DumpState());
            return;
        }

        if (trimmed.Equals("hldebug folder", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(TreasureHighLowDiagnosticService.DiagnosticDirectory);
            OpenPath(TreasureHighLowDiagnosticService.DiagnosticDirectory);
            PrintStatus($"Opened Higher/Lower diagnostics folder: {TreasureHighLowDiagnosticService.DiagnosticDirectory}");
            return;
        }

        if (trimmed.StartsWith("hldebug card ", StringComparison.OrdinalIgnoreCase))
        {
            var cardArgs = trimmed["hldebug card ".Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var role = cardArgs.Length >= 2 ? cardArgs[1] : "current";
            if (cardArgs.Length is < 1 or > 2
                || !int.TryParse(cardArgs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var card)
                || !TreasureHighLowDiagnosticService.TagKnownCard(card, role))
            {
                PrintStatus("Higher/Lower card tag must be: /ads hldebug card <1-9> [current|next|previous].");
                return;
            }

            PrintStatus($"Higher/Lower known-card tag queued: card={card} role={TreasureHighLowDiagnosticService.NormalizeKnownCardRole(role)}.");
            return;
        }

        if (trimmed.StartsWith("hldebug board ", StringComparison.OrdinalIgnoreCase))
        {
            var boardText = trimmed["hldebug board ".Length..].Trim();
            var boardArgs = boardText.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (boardArgs.Length < 2
                || !TreasureHighLowDiagnosticService.TagKnownBoard(
                    boardArgs[0],
                    boardArgs[1],
                    boardArgs.Length >= 3 ? boardArgs[2] : string.Empty))
            {
                Log.Information($"{TreasureHighLowDiagnosticService.LogPrefix} invalid board tag args text='{boardText.Replace("'", "\\'", StringComparison.Ordinal)}'.");
                PrintStatus("Higher/Lower board tag must be: /ads hldebug board <left> <right> [label...], where cards are 1-9, blank, or unknown.");
                return;
            }

            var left = TreasureHighLowDiagnosticService.NormalizeKnownBoardCardToken(boardArgs[0]);
            var right = TreasureHighLowDiagnosticService.NormalizeKnownBoardCardToken(boardArgs[1]);
            var label = boardArgs.Length >= 3 ? boardArgs[2].Trim() : string.Empty;
            if (label.Length > 80)
                label = label[..80];

            PrintStatus($"Higher/Lower board tag queued: left={left} right={right} label='{label}'.");
            return;
        }

        if (trimmed.Equals("hldebug on", StringComparison.OrdinalIgnoreCase))
        {
            TreasureHighLowDiagnosticService.SetEnabled(true);
            PrintStatus("Higher/Lower diagnostics enabled.");
            return;
        }

        if (trimmed.Equals("hldebug off", StringComparison.OrdinalIgnoreCase))
        {
            TreasureHighLowDiagnosticService.SetEnabled(false);
            PrintStatus("Higher/Lower diagnostics disabled.");
            return;
        }

        if (trimmed.Equals("hlauto status", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("hlauto", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(HigherLowerAutomationService.Status);
            return;
        }

        if (trimmed.Equals("hlauto on", StringComparison.OrdinalIgnoreCase))
        {
            HigherLowerAutomationService.SetEnabled(true);
            PrintStatus(HigherLowerAutomationService.Status);
            return;
        }

        if (trimmed.Equals("hlauto off", StringComparison.OrdinalIgnoreCase))
        {
            HigherLowerAutomationService.SetEnabled(false);
            PrintStatus(HigherLowerAutomationService.Status);
            return;
        }

        if (trimmed.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            StopOwnership();
            return;
        }

        ToggleMainUi();
    }

    private void HandleDebugCommand(string trimmed)
    {
        var mode = trimmed.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? "status"
            : trimmed["debug ".Length..].Trim();

        if (mode.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(DebugStrafeService.Enable());
            return;
        }

        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(DebugStrafeService.Disable("command"));
            return;
        }

        if (mode.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(DebugStrafeService.Release("command"));
            return;
        }

        if (mode.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(DebugStrafeService.Status);
            return;
        }

        PrintStatus("Debug mode must be: /ads debug on|off|status|release.");
    }

    private void SetupDtrBar()
    {
        dtrEntry = DtrBar.Get(PluginInfo.ShortDisplayName);
        dtrEntry.OnClick = _ => OpenMainUi();
    }

    private bool CanStartManualUtility(string actionLabel)
    {
        if (ExecutionService.IsOwned)
        {
            PrintStatus($"Cannot start {actionLabel} while ADS owns active duty execution.");
            return false;
        }

        if (InnEntryService.IsRunning)
        {
            PrintStatus($"Cannot start {actionLabel} while /ads enterinn is running.");
            return false;
        }

        return true;
    }

    public void UpdateDtrBar()
    {
        if (dtrEntry is null)
            return;

        dtrEntry.Shown = Configuration.DtrBarEnabled;
        if (!Configuration.DtrBarEnabled)
            return;

        var glyph = Configuration.PluginEnabled ? Configuration.DtrIconEnabled : Configuration.DtrIconDisabled;
        var state = ExecutionService.CurrentMode switch
        {
            OwnershipMode.Observing => "Obs",
            OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside => "Run",
            OwnershipMode.Leaving => "Leave",
            OwnershipMode.Failed => "Fail",
            _ => Configuration.PluginEnabled ? "On" : "Off",
        };
        var phase = ExecutionService.CurrentPhase switch
        {
            ExecutionPhase.ObservingOnly => "Observe",
            ExecutionPhase.OutsideQueue => "Queue",
            ExecutionPhase.AwaitingSupportedPilotDuty => "WaitPilot",
            ExecutionPhase.TransitionHold => "Transit",
            ExecutionPhase.CombatHold => "Combat",
            ExecutionPhase.ReadyForMonsterObjective => "Monster",
            ExecutionPhase.NavigatingToMonsterObjective => "MonNav",
            ExecutionPhase.ReadyForInteractableObjective => "Interact",
            ExecutionPhase.NavigatingToRecoveryObjective => "RecNav",
            ExecutionPhase.RecoveryHint => "Recover",
            ExecutionPhase.NavigatingToFrontierObjective => "FrontNav",
            ExecutionPhase.FrontierHint => "Frontier",
            ExecutionPhase.NavigatingToMapXzDestination => "MapXZ",
            ExecutionPhase.MapXzDestinationHint => "MapXZ",
            ExecutionPhase.NavigatingToXyzDestination => "XYZ",
            ExecutionPhase.XyzDestinationHint => "XYZ",
            ExecutionPhase.NavigatingToFollowObjective => "Follow",
            ExecutionPhase.ReadyForFollowObjective => "Follow",
            ExecutionPhase.MountedDutyCombat => "MountAtk",
            ExecutionPhase.WaitingForTruth => "Wait",
            ExecutionPhase.LeavingDuty => "Leaving",
            ExecutionPhase.Failure => "Fail",
            _ => "Idle",
        };

        dtrEntry.Text = Configuration.DtrBarMode switch
        {
            1 => new SeString(new TextPayload($"{glyph} ADS:{state}/{phase}")),
            2 => new SeString(new TextPayload(glyph)),
            _ => new SeString(new TextPayload($"ADS: {state}/{phase}")),
        };

        var tooltipDuty = DutyContextService.Current.CurrentDuty?.EnglishName ?? "No active duty";
        dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName} {state}/{phase}. {tooltipDuty}. Click to open the main window."));
    }

    private static bool ApplyConfigurationMigrations(Configuration configuration)
    {
        var changed = false;
        if (configuration.Version < 1)
        {
            configuration.Version = 1;
            changed = true;
        }

        if (configuration.Version < 2)
        {
            configuration.ConsiderTreasureCoffers = true;
            configuration.Version = 2;
            changed = true;
        }

        if (configuration.Version < 3)
        {
            configuration.Version = 3;
            changed = true;
        }

        if (configuration.Version < 4)
        {
            configuration.Version = 4;
            changed = true;
        }

        if (configuration.Version < 5)
        {
            configuration.TreasureDoorJiggleRecoveryEnabled = true;
            configuration.Version = 5;
            changed = true;
        }

        if (configuration.Version < 6)
        {
            configuration.ResetCameraBeforeInteractEnabled = true;
            configuration.Version = 6;
            changed = true;
        }

        if (configuration.Version < 7)
        {
            configuration.ProcessDialogRulesOutsideOwnedDuty = true;
            configuration.Version = 7;
            changed = true;
        }

        if (configuration.Version < 8)
        {
            configuration.HigherLowerDiagnosticsEnabled = false;
            configuration.Version = 8;
            changed = true;
        }

        if (configuration.Version < 9)
        {
            configuration.HigherLowerDiagnosticsEnabled = false;
            configuration.Version = 9;
            changed = true;
        }

        if (configuration.Version < 10)
        {
            configuration.HigherLowerAutomationEnabled = false;
            configuration.Version = 10;
            changed = true;
        }

        if (configuration.Version < 11)
        {
            configuration.HigherLowerDiagnosticsEnabled = true;
            configuration.HigherLowerAutomationEnabled = true;
            configuration.Version = 11;
            changed = true;
        }

        if (configuration.Version < 12)
        {
            configuration.HigherLowerVfxDataminingEnabled = false;
            configuration.Version = 12;
            changed = true;
        }

        if (configuration.Version < 13)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionQueenLunatenderDisabled = false;
            configuration.ReflectionHuntsDisabled = false;
            configuration.ReflectionMaxLoadDistanceMinimized = false;
            configuration.ReflectionMinimizedMaxLoadDistance = BmrReflectionService.DefaultMinimizedMaxLoadDistance;
            configuration.ReflectionHasOriginalMaxLoadDistance = false;
            configuration.ReflectionOriginalMaxLoadDistance = BmrReflectionService.DefaultFallbackMaxLoadDistance;
            configuration.Version = 13;
            changed = true;
        }

        if (configuration.Version < 14)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.Version = 14;
            changed = true;
        }

        if (configuration.Version < 15)
        {
            configuration.OpenQuickControlsOnLoad = false;
            configuration.Version = 15;
            changed = true;
        }

        if (configuration.Version < 16)
        {
            configuration.BmraiTreasureFollowCleanupPending = true;
            configuration.Version = 16;
            changed = true;
        }

        if (configuration.Version < 17)
        {
            configuration.LootMode = LootRollMode.Off;
            configuration.LootRegistrableNeedingEnabled = false;
            configuration.LootRegistrableMountsEnabled = true;
            configuration.LootRegistrableMinionsEnabled = true;
            configuration.LootRegistrableFashionAccessoriesEnabled = true;
            configuration.LootRegistrableFacewearEnabled = true;
            configuration.LootRegistrableOrchestrionRollsEnabled = true;
            configuration.LootRegistrableFadedOrchestrionCopiesEnabled = true;
            configuration.LootRegistrableEmotesHairstylesEnabled = true;
            configuration.LootRegistrableBardingsEnabled = true;
            configuration.LootRegistrableTripleTriadCardsEnabled = true;
            configuration.Version = 17;
            changed = true;
        }

        var clampedDtrBarMode = Math.Clamp(configuration.DtrBarMode, 0, 2);
        if (configuration.DtrBarMode != clampedDtrBarMode)
        {
            configuration.DtrBarMode = clampedDtrBarMode;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(configuration.DtrIconEnabled))
        {
            configuration.DtrIconEnabled = Configuration.DefaultDtrIconEnabled;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(configuration.DtrIconDisabled))
        {
            configuration.DtrIconDisabled = Configuration.DefaultDtrIconDisabled;
            changed = true;
        }

        if (!float.IsFinite(configuration.ReflectionMinimizedMaxLoadDistance) || configuration.ReflectionMinimizedMaxLoadDistance <= 0f)
        {
            configuration.ReflectionMinimizedMaxLoadDistance = BmrReflectionService.DefaultMinimizedMaxLoadDistance;
            changed = true;
        }
        else
        {
            var clampedMinimizedMaxLoadDistance = Math.Clamp(
                configuration.ReflectionMinimizedMaxLoadDistance,
                0.1f,
                BmrReflectionService.DefaultFallbackMaxLoadDistance);
            if (Math.Abs(configuration.ReflectionMinimizedMaxLoadDistance - clampedMinimizedMaxLoadDistance) > 0.001f)
            {
                configuration.ReflectionMinimizedMaxLoadDistance = clampedMinimizedMaxLoadDistance;
                changed = true;
            }
        }

        if (!float.IsFinite(configuration.ReflectionOriginalMaxLoadDistance) || configuration.ReflectionOriginalMaxLoadDistance <= 0f)
        {
            configuration.ReflectionOriginalMaxLoadDistance = BmrReflectionService.DefaultFallbackMaxLoadDistance;
            configuration.ReflectionHasOriginalMaxLoadDistance = false;
            changed = true;
        }

        return changed;
    }
}
