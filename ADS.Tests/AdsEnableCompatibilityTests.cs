using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ADS.Services;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

[Collection("ADS configuration IPC")]
public sealed class AdsEnableCompatibilityTests
{
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
                _ => false, () => false, _ => false, (_, _) => false, _ => false, () => false,
                () => false, () => false, () => "{}", () => "{}", () => "{}", Invoke,
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

    public class IpcProxy : DispatchProxy
    {
        public readonly Dictionary<string, IpcProxy> Providers = [];
        public readonly List<string> Messages = [];
        public Delegate? Callback;
        public int Saves;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "GetIpcProvider":
                    var provider = DispatchProxy.Create(method.ReturnType, typeof(IpcProxy));
                    Providers[(string)args![0]!] = (IpcProxy)provider;
                    return provider;
                case "RegisterFunc": Callback = (Delegate)args![0]!; return null;
                case "SavePluginConfig": ++Saves; return null;
                case "Information": Messages.Add((string)args![0]!); return null;
            }
            return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType
                ? null : Activator.CreateInstance(method.ReturnType);
        }
    }
}

[CollectionDefinition("ADS configuration IPC", DisableParallelization = true)]
public sealed class AdsConfigurationIpcCollection;
