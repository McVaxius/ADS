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
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
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
    public MapFlagService MapFlagService { get; }
    public InnEntryService InnEntryService { get; }
    public UtilityAutomationService UtilityAutomationService { get; }
    public RemoteJsonUpdateService RemoteJsonUpdateService { get; }
    public TreasureDungeonRoleDetector TreasureDungeonRoleDetector { get; }
    public TreasureHighLowDiagnosticService TreasureHighLowDiagnosticService { get; }
    public HigherLowerServerEventTraceService HigherLowerServerEventTraceService { get; }
    public HigherLowerVfxTraceService HigherLowerVfxTraceService { get; }
    public HigherLowerCardVfxSolverService HigherLowerCardVfxSolverService { get; }
    public HigherLowerAutomationService HigherLowerAutomationService { get; }

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly ObjectExplorerWindow objectExplorerWindow;
    private readonly GhostListWindow ghostListWindow;
    private readonly FrontierLabelWindow frontierLabelWindow;
    private readonly QuickControlWindow quickControlWindow;
    private readonly ObjectRuleEditorWindow objectRuleEditorWindow;
    private readonly DialogRuleEditorWindow dialogRuleEditorWindow;
    private readonly HigherLowerWindow higherLowerWindow;
    private readonly ServerEventExplorerWindow serverEventExplorerWindow;
    private readonly VfxExplorerWindow vfxExplorerWindow;
    private IDtrBarEntry? dtrEntry;
    private string objectExplorerStatus = "Ready.";

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
        TreasureDungeonRoleDetector = new TreasureDungeonRoleDetector(PluginInterface, ObjectTable, Log, configDirectory);
        RemoteJsonUpdateService = new RemoteJsonUpdateService(Log, configDirectory);
        RemoteJsonUpdateService.TryStartMissingUpdate("startup");

        DutyCatalogService = new DutyCatalogService(DataManager, Log, configDirectory);
        DutyContextService = new DutyContextService(ClientState, Condition, DutyCatalogService);
        ObjectPriorityRuleService = new ObjectPriorityRuleService(Log, DataManager, configDirectory);
        DialogYesNoRuleService = new DialogYesNoRuleService(Log, configDirectory);
        ObservationMemoryService = new ObservationMemoryService(ObjectTable, PartyList, Log, ObjectPriorityRuleService);
        DungeonFrontierService = new DungeonFrontierService(DataManager, ObjectTable, Log, ObjectPriorityRuleService);
        ObjectivePlannerService = new ObjectivePlannerService(ObjectTable, ObjectPriorityRuleService, DungeonFrontierService, Log);
        MapFlagService = new MapFlagService(DataManager, ClientState, Condition, Log);
        ExecutionService = new ExecutionService(DataManager, ObjectTable, TargetManager, CommandManager, ObservationMemoryService, DungeonFrontierService, MapFlagService, ObjectPriorityRuleService, Configuration, Log);
        DialogAutomationService = new DialogAutomationService(GameGui, DialogYesNoRuleService, Log);
        TreasureHighLowDiagnosticService = new TreasureHighLowDiagnosticService(GameGui, ObjectTable, ClientState, DataManager, Log, Configuration, configDirectory);
        HigherLowerServerEventTraceService = new HigherLowerServerEventTraceService(ObjectTable, ClientState, SigScanner, GameInteropProvider, TreasureHighLowDiagnosticService, Log);
        HigherLowerVfxTraceService = new HigherLowerVfxTraceService(ObjectTable, ClientState, TreasureHighLowDiagnosticService, Log);
        HigherLowerCardVfxSolverService = new HigherLowerCardVfxSolverService(TreasureHighLowDiagnosticService, HigherLowerVfxTraceService, DataManager, Log);
        HigherLowerVfxTraceService.AttachCardSolver(HigherLowerCardVfxSolverService);
        HigherLowerAutomationService = new HigherLowerAutomationService(TreasureHighLowDiagnosticService, HigherLowerCardVfxSolverService, ObjectTable, TargetManager, CommandManager, Configuration, GameGui, Log);
        InnEntryService = new InnEntryService(DataManager, ObjectTable, TargetManager, CommandManager, ClientState, Condition, Log);
        UtilityAutomationService = new UtilityAutomationService(DataManager, ObjectTable, TargetManager, CommandManager, ClientState, Condition, Log);
        AdsIpcService = new AdsIpcService(
            PluginInterface,
            StartDutyFromOutside,
            StartDutyFromInside,
            ResumeDutyFromInside,
            LeaveDuty,
            StartRepair,
            GetStatusJson,
            GetCurrentAnalysisJson);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        objectExplorerWindow = new ObjectExplorerWindow(this);
        ghostListWindow = new GhostListWindow(this);
        frontierLabelWindow = new FrontierLabelWindow(this);
        quickControlWindow = new QuickControlWindow(this);
        objectRuleEditorWindow = new ObjectRuleEditorWindow(this);
        dialogRuleEditorWindow = new DialogRuleEditorWindow(this);
        higherLowerWindow = new HigherLowerWindow(this);
        serverEventExplorerWindow = new ServerEventExplorerWindow(this);
        vfxExplorerWindow = new VfxExplorerWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(objectExplorerWindow);
        WindowSystem.AddWindow(ghostListWindow);
        WindowSystem.AddWindow(frontierLabelWindow);
        WindowSystem.AddWindow(quickControlWindow);
        WindowSystem.AddWindow(objectRuleEditorWindow);
        WindowSystem.AddWindow(dialogRuleEditorWindow);
        WindowSystem.AddWindow(higherLowerWindow);
        WindowSystem.AddWindow(serverEventExplorerWindow);
        WindowSystem.AddWindow(vfxExplorerWindow);

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
        RemoteJsonUpdateService.Dispose();
        WindowSystem.RemoveAllWindows();
        dtrEntry?.Remove();
        configWindow.Dispose();
        mainWindow.Dispose();
        objectExplorerWindow.Dispose();
        ghostListWindow.Dispose();
        frontierLabelWindow.Dispose();
        quickControlWindow.Dispose();
        objectRuleEditorWindow.Dispose();
        dialogRuleEditorWindow.Dispose();
        higherLowerWindow.Dispose();
        serverEventExplorerWindow.Dispose();
        vfxExplorerWindow.Dispose();
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
        => quickControlWindow.IsOpen = !quickControlWindow.IsOpen;

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

    public void ToggleServerEventExplorerUi()
        => serverEventExplorerWindow.IsOpen = !serverEventExplorerWindow.IsOpen;

    public void OpenServerEventExplorerUi()
        => serverEventExplorerWindow.IsOpen = true;

    public void ToggleVfxExplorerUi()
        => vfxExplorerWindow.IsOpen = !vfxExplorerWindow.IsOpen;

    public void OpenVfxExplorerUi()
        => vfxExplorerWindow.IsOpen = true;

    public void SaveConfiguration()
    {
        Configuration.Save();
        UpdateDtrBar();
    }

    public void ForceRemoteJsonUpdate()
        => RemoteJsonUpdateService.TryStartUpdate(force: true, "operator Update button");

    public void ResetWindowPositions()
    {
        mainWindow.QueueResetToOrigin();
        configWindow.QueueResetToOrigin();
        objectExplorerWindow.QueueResetToOrigin();
        ghostListWindow.QueueResetToOrigin();
        frontierLabelWindow.QueueResetToOrigin();
        quickControlWindow.QueueResetToOrigin();
        objectRuleEditorWindow.QueueResetToOrigin();
        dialogRuleEditorWindow.QueueResetToOrigin();
        higherLowerWindow.QueueResetToOrigin();
        serverEventExplorerWindow.QueueResetToOrigin();
        vfxExplorerWindow.QueueResetToOrigin();
    }

    public void JumpWindows()
    {
        mainWindow.QueueRandomVisibleJump();
        configWindow.QueueRandomVisibleJump();
        objectExplorerWindow.QueueRandomVisibleJump();
        ghostListWindow.QueueRandomVisibleJump();
        frontierLabelWindow.QueueRandomVisibleJump();
        quickControlWindow.QueueRandomVisibleJump();
        objectRuleEditorWindow.QueueRandomVisibleJump();
        dialogRuleEditorWindow.QueueRandomVisibleJump();
        higherLowerWindow.QueueRandomVisibleJump();
        serverEventExplorerWindow.QueueRandomVisibleJump();
        vfxExplorerWindow.QueueRandomVisibleJump();
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
        var result = ExecutionService.StartDutyFromOutside();
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool StartDutyFromInside()
    {
        QueueDutyOwnershipRemoteUpdate();
        InferAndApplyTreasureDungeonRole("inside start");
        var result = ExecutionService.StartDutyFromInside(DutyContextService.Current);
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool ResumeDutyFromInside()
    {
        QueueDutyOwnershipRemoteUpdate();
        InferAndApplyTreasureDungeonRole("inside resume");
        var result = ExecutionService.ResumeDutyFromInside(DutyContextService.Current);
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        return result;
    }

    public bool LeaveDuty()
    {
        var result = ExecutionService.LeaveDuty(DutyContextService.Current, Configuration.ConsiderTreasureCoffers);
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
        ExecutionService.Stop(DutyContextService.Current);
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

    public bool StartRepair(string mode)
    {
        var normalized = NormalizeRepairMode(mode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            PrintStatus("Repair mode must be self, npc, or npc-no-inn.");
            return false;
        }

        return normalized switch
        {
            "self" => StartSelfRepair(),
            "npc" => StartNpcRepair(),
            "npc-no-inn" => StartNpcRepairNoInn(),
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
                processDialogRulesOutsideOwnedDuty = Configuration.ProcessDialogRulesOutsideOwnedDuty,
                higherLowerVfxDataminingEnabled = Configuration.HigherLowerVfxDataminingEnabled,
                version = PluginInfo.GetVersion(),
                ownershipMode = ExecutionService.CurrentMode.ToString(),
                executionPhase = ExecutionService.CurrentPhase.ToString(),
                executionStatus = ExecutionService.LastStatus,
                treasureDungeonRole = ExecutionService.TreasureDungeonRole.ToString(),
                effectiveTreasureDungeonRole = DungeonFrontierService.EffectiveTreasureDungeonRole.ToString(),
                treasureDungeonRoleSource = ExecutionService.TreasureDungeonRoleSource,
                treasureDungeonRoleDetail = ExecutionService.TreasureDungeonRoleDetail,
                frontierRouteSource = DungeonFrontierService.CurrentTreasureRouteSource,
                frontierRouteKey = DungeonFrontierService.CurrentTarget?.Key,
                treasureFollowerEntryMapOpenerRoleActive = DungeonFrontierService.TreasureFollowerEntryMapOpenerRoleActive,
                treasureFollowerEntryProofDutyKey = DungeonFrontierService.TreasureFollowerEntryProofDutyKey,
                treasureFollowerHeldCandidateKey = DungeonFrontierService.TreasureFollowerHeldCandidateKey,
                treasureFollowerHeldCandidateName = DungeonFrontierService.TreasureFollowerHeldCandidateName,
                treasureFollowerHeldCandidateTransitObserved = DungeonFrontierService.TreasureFollowerHeldCandidateTransitObserved,
                treasureFollowerLastFailedCandidateKey = DungeonFrontierService.TreasureFollowerLastFailedCandidateKey,
                treasureFollowerLastFailedCandidateReason = DungeonFrontierService.TreasureFollowerLastFailedCandidateReason,
                treasureFollowerDoorFollowThroughActive = ExecutionService.TreasureFollowerDoorFollowThroughActive,
                treasureFollowerDoorFollowThroughCandidateKey = ExecutionService.TreasureFollowerDoorFollowThroughCandidateKey,
                treasureFollowerDoorFollowThroughCandidateName = ExecutionService.TreasureFollowerDoorFollowThroughCandidateName,
                treasureFollowerDoorFollowThroughTarget = ExecutionService.TreasureFollowerDoorFollowThroughTarget,
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
                treasureDungeonRole = ExecutionService.TreasureDungeonRole.ToString(),
                treasureDungeonRoleSource = ExecutionService.TreasureDungeonRoleSource,
                treasureDungeonRoleDetail = ExecutionService.TreasureDungeonRoleDetail,
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
                    treasureDungeonRole = DungeonFrontierService.TreasureDungeonRole.ToString(),
                    effectiveTreasureDungeonRole = DungeonFrontierService.EffectiveTreasureDungeonRole.ToString(),
                    treasureDungeonRoleSource = DungeonFrontierService.TreasureDungeonRoleSource,
                    treasureDungeonRoleDetail = DungeonFrontierService.TreasureDungeonRoleDetail,
                    treasureFollowerRetryCycle = DungeonFrontierService.TreasureFollowerRetryCycle,
                    treasureFollowerEntryMapOpenerRoleActive = DungeonFrontierService.TreasureFollowerEntryMapOpenerRoleActive,
                    treasureFollowerEntryProofDutyKey = DungeonFrontierService.TreasureFollowerEntryProofDutyKey,
                    treasureFollowerHeldCandidateKey = DungeonFrontierService.TreasureFollowerHeldCandidateKey,
                    treasureFollowerHeldCandidateName = DungeonFrontierService.TreasureFollowerHeldCandidateName,
                    treasureFollowerHeldCandidateTransitObserved = DungeonFrontierService.TreasureFollowerHeldCandidateTransitObserved,
                    treasureFollowerLastFailedCandidateKey = DungeonFrontierService.TreasureFollowerLastFailedCandidateKey,
                    treasureFollowerLastFailedCandidateReason = DungeonFrontierService.TreasureFollowerLastFailedCandidateReason,
                    treasureFollowerDoorFollowThroughActive = ExecutionService.TreasureFollowerDoorFollowThroughActive,
                    treasureFollowerDoorFollowThroughCandidateKey = ExecutionService.TreasureFollowerDoorFollowThroughCandidateKey,
                    treasureFollowerDoorFollowThroughCandidateName = ExecutionService.TreasureFollowerDoorFollowThroughCandidateName,
                    treasureFollowerDoorFollowThroughTarget = ExecutionService.TreasureFollowerDoorFollowThroughTarget,
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

    private void QueueDutyOwnershipRemoteUpdate()
        => RemoteJsonUpdateService.TryStartStaleUpdate("duty ownership");

    private void InferAndApplyTreasureDungeonRole(string reason)
    {
        var inference = TreasureDungeonRoleDetector.Infer();
        ExecutionService.SetTreasureDungeonRole(inference);
        DungeonFrontierService.SetTreasureDungeonRole(inference);
        Log.Information(
            $"[ADS] Treasure role inference for {reason}: role={inference.Role}, source={inference.Source}, character='{inference.CharacterKey}'. {inference.Detail}");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (RemoteJsonUpdateService.TryConsumeCompletedUpdate())
        {
            ObjectPriorityRuleService.Reload();
            DialogYesNoRuleService.Reload();
            DutyCatalogService.ReloadMaturity();
        }

        DutyContextService.Update(Configuration.PluginEnabled);
        HigherLowerServerEventTraceService.Update(DutyContextService.Current);
        HigherLowerVfxTraceService.Update(DutyContextService.Current);
        HigherLowerCardVfxSolverService.Update(DutyContextService.Current);
        ObjectPriorityRuleService.ReloadIfChanged();
        DialogYesNoRuleService.ReloadIfChanged();
        DialogAutomationService.Update(
            DutyContextService.Current,
            ExecutionService.CurrentMode,
            Configuration.PluginEnabled,
            Configuration.ProcessDialogRulesOutsideOwnedDuty);
        HigherLowerAutomationService.Update(DutyContextService.Current, ExecutionService.CurrentMode, Configuration.PluginEnabled);
        ExecutionService.SetHigherLowerAutomationHold(
            HigherLowerAutomationService.HoldMovement,
            HigherLowerAutomationService.Status,
            HigherLowerAutomationService.BlocksDutyExit,
            HigherLowerAutomationService.LastHigherLowerActivityUtc);

        if (DutyContextService.Current.IsUnsafeTransition)
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
            UpdateDtrBar();
            return;
        }

        ObservationMemoryService.Update(DutyContextService.Current, Configuration.ConsiderTreasureCoffers);
        DungeonFrontierService.Update(DutyContextService.Current, ObservationMemoryService.Current);
        ObjectivePlannerService.Update(
            DutyContextService.Current,
            ObservationMemoryService.Current,
            ExecutionService.CurrentMode,
            Configuration.ConsiderTreasureCoffers);
        ExecutionService.Update(
            DutyContextService.Current,
            ObjectivePlannerService.Current,
            ObservationMemoryService.Current,
            Configuration.PluginEnabled,
            Configuration.ConsiderTreasureCoffers,
            DialogAutomationService.DialogStatus);
        TreasureHighLowDiagnosticService.Update(
            DutyContextService.Current,
            ObservationMemoryService.Current,
            ObjectivePlannerService.Current,
            DialogAutomationService.DialogStatus);
        InnEntryService.Update();
        UtilityAutomationService.Update();
        UpdateDtrBar();
    }

    private void OnChatMessage(IHandleableChatMessage message)
        => ExecutionService.HandleChatMessage(message.Message.TextValue);

    private void OnDutyCompleted(IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(uint territoryId)
    {
        var context = DutyContextService.Current;
        var dutyName = context.CurrentDuty?.EnglishName ?? $"territory {territoryId}";
        if (!ExecutionService.IsOwned)
        {
            ObservationMemoryService.Reset();
            DungeonFrontierService.Reset();
            Log.Information($"[ADS] DutyCompleted event for {dutyName}; observation memory cleared while ADS was not executing.");
            return;
        }

        if (IsTreasureDungeonDutyCompletedNoise(context, territoryId))
        {
            UpdateDtrBar();
            Log.Information($"[ADS] DutyCompleted event for {dutyName}; ignored as treasure-dungeon chamber-complete noise while ADS keeps ownership.");
            return;
        }

        ObservationMemoryService.Reset();
        DungeonFrontierService.Reset();
        ExecutionService.CompleteDuty(dutyName);
        PrintStatus(ExecutionService.LastStatus);
        UpdateDtrBar();
        Log.Information($"[ADS] DutyCompleted event for {dutyName}; ownership released and observation memory cleared.");
    }

    private static bool IsTreasureDungeonDutyCompletedNoise(DutyContextSnapshot context, uint territoryId)
        => context.InInstancedDuty
           && (context.CurrentDuty?.Category == DutyCategory.TreasureDungeon
               || TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId)
               || TreasureDungeonData.IsSupportedDutyTerritory(territoryId));

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
                "/ads rules - toggle the rules editor\n" +
                "/ads dialogs - toggle the dialog rules editor\n" +
                "/ads hl - toggle the Higher/Lower calibration window\n" +
                "/ads events - toggle the server event explorer\n" +
                "/ads vfx - toggle the VFX explorer\n" +
                "/ads mapeffects - alias for /ads events\n" +
                "/ads ws - reset windows to 1,1\n" +
                "/ads j - jump windows to visible random positions\n" +
                "/ads outside - queue outside ownership\n" +
                "/ads inside - claim ownership inside duty\n" +
                "/ads resume - resume inside duty\n" +
                "/ads leave - request leave state - if chests nearby it will grab them then wait 10 seconds\n" +
                "/ads enterinn - move to a nearby innkeeper and enter the inn\n" +
                "/ads repair self|npc|npc-no-inn - start reusable repair automation\n" +
                "/ads selfrepair - open self-repair and repair equipped gear\n" +
                "/ads npcrepair - move to a nearby repair NPC and repair equipped gear\n" +
                "/ads npcrepair noinn - NPC repair without inn fallback\n" +
                "/ads extractmateria - extract ready materia from gear\n" +
                "/ads desynthfrominventory - desynth inventory-only items\n" +
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
            PrintStatus("Repair mode must be self, npc, or npc-no-inn.");
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

        return changed;
    }
}
