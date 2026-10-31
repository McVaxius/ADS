using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

[Collection("ADS configuration IPC")]
public sealed class AdsEnableCompatibilityTests
{
    [Theory]
    [InlineData(900u, 900u, true, false, false, true)]
    [InlineData(1163u, 1163u, true, false, false, true)]
    [InlineData(900u, 900u, false, false, false, false)]
    [InlineData(129u, 900u, false, false, false, true)]
    [InlineData(129u, 1163u, false, true, false, false)]
    [InlineData(1163u, 1163u, true, false, true, false)]
    [InlineData(512u, 512u, true, false, false, false)]
    public void ActualNpcSaleAdmissionRetainsDutyInnAndOtherUtilityExclusions(
        uint liveTerritory, uint observedTerritory, bool localOnly, bool dutyOwned, bool innOwned, bool allowed)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var context = (DutyContextService)RuntimeHelpers.GetUninitializedObject(typeof(DutyContextService));
        typeof(DutyContextService).GetField("<Current>k__BackingField", flags)!.SetValue(context,
            Newtonsoft.Json.JsonConvert.DeserializeObject<DutyContextSnapshot>($"{{\"TerritoryTypeId\":{observedTerritory}}}"));
        var execution = (ExecutionService)RuntimeHelpers.GetUninitializedObject(typeof(ExecutionService));
        var mode = typeof(ExecutionService).GetField("<CurrentMode>k__BackingField", flags)!;
        mode.SetValue(execution, Enum.Parse(mode.FieldType, dutyOwned ? "OwnedStartInside" : "Idle"));
        var inn = (InnEntryService)RuntimeHelpers.GetUninitializedObject(typeof(InnEntryService));
        var state = typeof(InnEntryService).GetField("state", flags)!;
        state.SetValue(inn, Enum.ToObject(state.FieldType, innOwned ? 1 : 0));
        typeof(Plugin).GetField("<DutyContextService>k__BackingField", flags)!.SetValue(plugin, context);
        typeof(Plugin).GetField("<ExecutionService>k__BackingField", flags)!.SetValue(plugin, execution);
        typeof(Plugin).GetField("<InnEntryService>k__BackingField", flags)!.SetValue(plugin, inn);
        var clientProperty = typeof(Plugin).GetProperty("ClientState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var chatProperty = typeof(Plugin).GetProperty("ChatGui", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousClient = clientProperty.GetValue(null);
        var previousChat = chatProperty.GetValue(null);
        var client = DispatchProxy.Create<IClientState, IpcProxy>();
        ((IpcProxy)(object)client).TerritoryType = liveTerritory;
        clientProperty.SetValue(null, client);
        chatProperty.SetValue(null, DispatchProxy.Create<IChatGui, IpcProxy>());
        try
        {
            var admission = typeof(Plugin).GetMethod("CanStartManualUtility", flags)!;
            Assert.Equal(allowed, (bool)admission.Invoke(plugin, ["NPC selling", true, localOnly])!);
            Assert.False((bool)admission.Invoke(plugin, ["NPC repair", false, localOnly])!);
        }
        finally
        {
            clientProperty.SetValue(null, previousClient);
            chatProperty.SetValue(null, previousChat);
        }
    }

    [Fact]
    public void OptInUiProfilerCatchesReportedRangeWithoutLoggingEveryDrawAndClearsWithFrameworkState()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var configuration = new Configuration();
        Assert.False(configuration.FrameworkHitchProfilerEnabled);
        typeof(Plugin).GetField("<Configuration>k__BackingField", flags)!.SetValue(plugin, configuration);
        typeof(Plugin).GetField("<DutyContextService>k__BackingField", flags)!.SetValue(plugin,
            new DutyContextService(null!, null!, null!, null!));
        typeof(Plugin).GetField("<ExecutionService>k__BackingField", flags)!.SetValue(plugin,
            RuntimeHelpers.GetUninitializedObject(typeof(ExecutionService)));
        typeof(Plugin).GetField("<WindowSystem>k__BackingField", flags)!.SetValue(plugin,
            new Dalamud.Interface.Windowing.WindowSystem("ADS profiler regression"));

        var log = DispatchProxy.Create<IPluginLog, IpcProxy>();
        var messages = ((IpcProxy)(object)log).Messages;
        var logProperty = typeof(Plugin).GetProperty("Log", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousLog = logProperty.GetValue(null);
        logProperty.SetValue(null, log);
        try
        {
            var report = typeof(Plugin).GetMethod("ReportUiSlowDraw", flags)!;
            var lastMs = typeof(Plugin).GetField("lastUiSlowDrawMs", flags)!;
            var nextLog = typeof(Plugin).GetField("nextUiSlowLogUtc", flags)!;
            report.Invoke(plugin, [9.9d]);
            Assert.Empty(messages);
            Assert.Equal(0d, lastMs.GetValue(plugin));

            var before = DateTime.UtcNow;
            report.Invoke(plugin, [10d]);
            var after = DateTime.UtcNow;
            Assert.Contains("UI draw slow", Assert.Single(messages));
            Assert.InRange((DateTime)nextLog.GetValue(plugin)!, before.AddSeconds(5), after.AddSeconds(5));
            report.Invoke(plugin, [15d]);
            report.Invoke(plugin, [30d]);
            Assert.Single(messages);
            Assert.Equal(30d, lastMs.GetValue(plugin)); // Retain evidence during the log cooldown.

            nextLog.SetValue(plugin, DateTime.MinValue);
            report.Invoke(plugin, [15d]);
            Assert.Equal(2, messages.Count);
            Assert.Equal(15d, lastMs.GetValue(plugin));

            typeof(Plugin).GetMethod("ClearFrameworkHitchState", flags)!.Invoke(plugin, null);
            Assert.Equal(0d, lastMs.GetValue(plugin));
            Assert.Equal(DateTime.MinValue, nextLog.GetValue(plugin));
            Assert.Equal(DateTime.MinValue, typeof(Plugin).GetField("lastUiSlowDrawUtc", flags)!.GetValue(plugin));
        }
        finally { logProperty.SetValue(null, previousLog); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DutyTitleCallbacksReadCurrentGuardsAndIgnoreNonLeftClicks(bool quickControls)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).GetField("<Configuration>k__BackingField", flags)!.SetValue(plugin, new Configuration());
        var contextService = (DutyContextService)RuntimeHelpers.GetUninitializedObject(typeof(DutyContextService));
        typeof(Plugin).GetField("<DutyContextService>k__BackingField", flags)!.SetValue(plugin, contextService);
        var execution = new ExecutionService(null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, plugin.Configuration);
        execution.RaidPolicy.QueryRuntimeStateJson = () => "{\"schemaVersion\":1,\"enabled\":false}";
        typeof(Plugin).GetField("<ExecutionService>k__BackingField", flags)!.SetValue(plugin, execution);
        void SetContext(bool inside, uint territory)
            => typeof(DutyContextService).GetField("<Current>k__BackingField", flags)!.SetValue(contextService,
                Newtonsoft.Json.JsonConvert.DeserializeObject<DutyContextSnapshot>(
                    $"{{\"BoundByDuty\":{inside.ToString().ToLowerInvariant()},\"TerritoryTypeId\":{territory}}}"));
        void Click(object button, string mouseButton)
        {
            var type = button.GetType();
            var callback = (Delegate)(type.GetProperty("Click")?.GetValue(button)
                ?? type.GetField("Click")!.GetValue(button))!;
            callback.DynamicInvoke(Enum.Parse(callback.Method.GetParameters()[0].ParameterType, mouseButton));
        }
        var clientStateProperty = typeof(Plugin).GetProperty("ClientState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousClientState = clientStateProperty.GetValue(null);
        clientStateProperty.SetValue(null, DispatchProxy.Create<IClientState, IpcProxy>());
        try
        {
            SetContext(false, 0);
            Window window = quickControls ? new QuickControlWindow(plugin) : new MainWindow(plugin);
            var buttons = window.TitleBarButtons.OrderByDescending(button => button.Priority).ToArray();
            Assert.Equal(6, buttons.Length);
            foreach (var button in buttons) Click(button, "Right");
            // Execution can read its inert Raid policy, but its action collaborators are absent:
            // a rejected click cannot prepare work, change ownership or save configuration.
            Click(buttons[3], "Left");
            Click(buttons[4], "Left");
            Assert.Null(plugin.GetDutyUiActionBlocker("Start Outside"));
            Assert.Contains("requires being inside", plugin.GetDutyUiActionBlocker("Start Inside"));
            Assert.Contains("requires being inside", plugin.GetDutyUiActionBlocker("Resume"));
            SetContext(true, 900);
            foreach (var index in new[] { 2, 3, 4 }) Click(buttons[index], "Left");
            Assert.Equal(AutomationTerritoryPolicy.InactiveStatus, plugin.GetDutyUiActionBlocker("Start Outside"));
            Assert.Null(plugin.GetDutyUiActionBlocker("Stop"));
            SetContext(true, 1);
            Assert.Null(plugin.GetDutyUiActionBlocker("Start Inside")); // Live duty truth needs no catalog row.
            Assert.Null(plugin.GetDutyUiActionBlocker("Resume"));
        }
        finally { clientStateProperty.SetValue(null, previousClientState); }
    }

    [Fact]
    public void NpcSaleCharacterChangeRecordsFailureWithoutTouchingTheNewCharactersUiOrAutomation()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var utility = (UtilityAutomationService)RuntimeHelpers.GetUninitializedObject(typeof(UtilityAutomationService));
        foreach (var name in new[] { "npcSaleRequest", "npcSaleCommandSent", "npcSaleBusyObserved" })
            typeof(UtilityAutomationService).GetField(name, flags)!.SetValue(utility, true);
        typeof(UtilityAutomationService).GetField("npcSaleCharacter", flags)!.SetValue(utility, 8UL);
        typeof(UtilityAutomationService).GetField("npcSaleStatus", flags)!.SetValue(utility,
            new ADS.Models.NpcSaleStatusSnapshot("previous-character-sale", true, false, null, "Running"));
        var playerStateProperty = typeof(Plugin).GetProperty("PlayerState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousPlayerState = playerStateProperty.GetValue(null);
        playerStateProperty.SetValue(null, DispatchProxy.Create<IPlayerState, IpcProxy>());
        try
        {
            // UI, command and IPC collaborators are deliberately absent: a character change
            // must finish the old receipt without using any of the new character's controls.
            typeof(UtilityAutomationService).GetMethod("FinishNpcSale", flags)!
                .Invoke(utility, [false, "The NPC sale character changed."]);
            Assert.False(utility.NpcSaleStatus.Running);
            Assert.True(utility.NpcSaleStatus.Done);
            Assert.False(utility.NpcSaleStatus.Succeeded);
            Assert.Equal("previous-character-sale", utility.NpcSaleStatus.OperationId);
        }
        finally { playerStateProperty.SetValue(null, previousPlayerState); }
    }

    [Fact]
    public void NpcSaleCannotCompleteFromRepairReadinessOrCancelAnotherOperation()
    {
        var utility = (UtilityAutomationService)RuntimeHelpers.GetUninitializedObject(typeof(UtilityAutomationService));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(UtilityAutomationService).GetField("npcSaleRequest", flags)!.SetValue(utility, true);
        typeof(UtilityAutomationService).GetField("npcSaleStatus", flags)!.SetValue(utility,
            new ADS.Models.NpcSaleStatusSnapshot("owned-sale", true, false, null, "Running"));
        var completed = (bool)typeof(UtilityAutomationService).GetMethod("TryCompleteRepairIfFinished", flags)!
            .Invoke(utility, ["Gear is repaired"])!;
        Assert.False(completed);
        Assert.False(utility.CancelNpcSale("another-sale"));
        Assert.True(utility.NpcSaleStatus.Running);
        Assert.Null(utility.NpcSaleStatus.Succeeded);
        Assert.False(utility.IsLocalNpcSaleRunning);
        var task = typeof(UtilityAutomationService).GetField("activeTask", flags)!;
        var repairMode = typeof(UtilityAutomationService).GetField("activeNpcRepairMode", flags)!;
        task.SetValue(utility, Enum.Parse(task.FieldType, "NpcRepair"));
        repairMode.SetValue(utility, Enum.Parse(repairMode.FieldType, "NoInn"));
        Assert.False(utility.IsLocalNpcSaleRunning);
        repairMode.SetValue(utility, Enum.Parse(repairMode.FieldType, "NoTeleportNoInn"));
        Assert.True(utility.IsLocalNpcSaleRunning);
        typeof(UtilityAutomationService).GetMethod("FinishNpcSale", flags)!.Invoke(utility, [true, "Verified sale"]);
        Assert.False(utility.IsLocalNpcSaleRunning);
    }

    [Fact]
    public void BothRegisteredConfigurationEndpointsIgnoreDisableAndLogCallerBeforeApplyingPatch()
    {
        var pi = DispatchProxy.Create<IDalamudPluginInterface, IpcProxy>();
        var proxy = (IpcProxy)(object)pi;
        var log = DispatchProxy.Create<IPluginLog, IpcProxy>();
        var logProxy = (IpcProxy)(object)log;
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var configuration = new Configuration();
        typeof(Plugin).GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(plugin, configuration);
        var api = new AdsOperatorApiService(plugin);
        typeof(Plugin).GetField("<AdsOperatorApiService>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(plugin, api);
        var piProperty = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousPi = piProperty.GetValue(null);
        piProperty.SetValue(null, pi);
        try
        {
            string Patch(string payload)
            {
                Assert.NotEmpty(logProxy.Messages); // Entry diagnostics precede application/dispatch.
                return plugin.PatchConfigurationJson(payload);
            }
            string Invoke(string action, string payload)
            {
                Assert.NotEmpty(logProxy.Messages);
                return plugin.Invoke(action, payload);
            }
            using var ipc = new AdsIpcService(pi,
                () => false, () => false, () => false, () => false, () => false, () => false,
                _ => false, () => false, _ => false, (_, _) => false, (_, _, _) => false, _ => false, (_, _, _) => false,
                (_, _) => false, () => "{}", _ => false,
                _ => "{}", (_, _, _) => false, _ => "{}", _ => false,
                _ => false, () => false,
                (_, _) => false, _ => false, _ => false, () => false,
                () => false, () => false, () => false, () => "{}", () => "{}", () => "{}", Invoke,
                plugin.GetConfigurationJson, Patch, () => "{}", () => "{}", () => "{}", () => "{}",
                _ => "{}", _ => "{}", _ => "{}", _ => false, _ => "{}", () => false, log);
            foreach (var endpoint in new[] { "ADS.PatchConfigurationJson", "ADS.Invoke" })
            foreach (var enabled in new[] { false, true })
            {
                logProxy.Messages.Clear();
                var payload = JsonSerializer.Serialize(new { PluginEnabled = enabled, higherLowerAutomationEnabled = false });
                var callback = proxy.Providers[endpoint].Callback!;
                var response = endpoint == "ADS.Invoke"
                    ? (string)callback.DynamicInvoke(" Configuration.Patch ", payload)!
                    : (string)callback.DynamicInvoke(payload)!;
                using var result = JsonDocument.Parse(response);
                Assert.True(result.RootElement.GetProperty("success").GetBoolean());
                Assert.True(configuration.PluginEnabled);
                Assert.False(configuration.HigherLowerAutomationEnabled); // Other patch fields still apply.
                using var status = JsonDocument.Parse(plugin.GetConfigurationJson());
                Assert.True(status.RootElement.GetProperty("PluginEnabled").GetBoolean());
                var message = Assert.Single(logProxy.Messages);
                Assert.Contains(endpoint, message);
                Assert.Contains("effective enabled=true", message);
                if (!enabled)
                {
                    Assert.Contains("disable ignored", message);
                    Assert.Contains(nameof(BothRegisteredConfigurationEndpointsIgnoreDisableAndLogCallerBeforeApplyingPatch), message);
                    Assert.Contains("ignored", result.RootElement.GetProperty("message").GetString());
                }
                else
                    Assert.DoesNotContain("Caller stack", message);
            }
            Assert.Equal(4, proxy.Saves);
        }
        finally
        {
            piProperty.SetValue(null, previousPi);
        }
    }

    [Fact]
    public void InventoryUtilityEndpointsForwardModesAndAdmissionResultsAndUnregisterOnDispose()
    {
        var pi = DispatchProxy.Create<IDalamudPluginInterface, IpcProxy>();
        var proxy = (IpcProxy)(object)pi;
        var moves = new List<bool>();
        var equips = 0;
        var allowed = false;
        bool Move(bool allUnprotected) { moves.Add(allUnprotected); return allowed; }
        bool Equip() { equips++; return allowed; }
        var ipc = new AdsIpcService(pi,
            () => false, () => false, () => false, () => false, () => false, () => false,
            _ => false, () => false, _ => false, (_, _) => false, (_, _, _) => false, _ => false, (_, _, _) => false,
            (_, _) => false, () => "{}", _ => false,
            _ => "{}", (_, _, _) => false, _ => "{}", _ => false,
            Move, Equip,
            (_, _) => false, _ => false, _ => false, () => false,
            () => false, () => false, () => false, () => "{}", () => "{}", () => "{}", (_, _) => "{}",
            () => "{}", _ => "{}", () => "{}", () => "{}", () => "{}", () => "{}",
            _ => "{}", _ => "{}", _ => "{}", _ => false, _ => "{}", () => false,
            DispatchProxy.Create<IPluginLog, IpcProxy>());
        try
        {
            Assert.Empty(moves); // Registration never dispatches an inventory action.
            Assert.Equal(0, equips);
            var move = Assert.IsType<Func<bool, bool>>(proxy.Providers["ADS.StartGearMove"].Callback);
            var equip = Assert.IsType<Func<bool>>(proxy.Providers["ADS.StartAutoEquip"].Callback);
            foreach (var admission in new[] { false, true })
            {
                allowed = admission;
                Assert.Equal(admission, move(false));
                Assert.Equal(admission, move(true));
                Assert.Equal(admission, equip());
            }
            Assert.Equal(new[] { false, true, false, true }, moves);
            Assert.Equal(2, equips);
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            using var capabilities = JsonDocument.Parse(new AdsOperatorApiService(plugin).GetCapabilitiesJson());
            Assert.Equal(1, capabilities.RootElement.GetProperty("equipmentMoving").GetInt32());
            Assert.Equal(1, capabilities.RootElement.GetProperty("autoEquip").GetInt32());
            Assert.Equal(new[] { "ADS.StartGearMove", "ADS.StartAutoEquip", "ADS.GetStatusJson", "ADS.CancelUtility" },
                capabilities.RootElement.GetProperty("equipmentUtilityIpc").EnumerateArray().Select(item => item.GetString()));
        }
        finally { ipc.Dispose(); }
        Assert.Null(proxy.Providers["ADS.StartGearMove"].Callback);
        Assert.Null(proxy.Providers["ADS.StartAutoEquip"].Callback);
    }

    public class IpcProxy : DispatchProxy
    {
        public readonly Dictionary<string, IpcProxy> Providers = [];
        public readonly List<string> Messages = [];
        public Delegate? Callback;
        public int Saves;
        public uint TerritoryType;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_UiBuilder": return DispatchProxy.Create(method.ReturnType, typeof(IpcProxy));
                case "get_TerritoryType": return TerritoryType;
                case "get_ContentId":
                    return 9UL;
                case "GetIpcProvider":
                    var provider = DispatchProxy.Create(method.ReturnType, typeof(IpcProxy));
                    Providers[(string)args![0]!] = (IpcProxy)provider;
                    return provider;
                case "RegisterFunc": Callback = (Delegate)args![0]!; return null;
                case "UnregisterFunc": Callback = null; return null;
                case "SavePluginConfig": ++Saves; return null;
                case "Information":
                case "Warning": Messages.Add((string)args![0]!); return null;
            }
            return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType
                ? null : Activator.CreateInstance(method.ReturnType);
        }
    }
}

[CollectionDefinition("ADS configuration IPC", DisableParallelization = true)]
public sealed class AdsConfigurationIpcCollection;
