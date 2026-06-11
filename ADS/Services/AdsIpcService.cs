using Dalamud.Plugin;

namespace ADS.Services;

public sealed class AdsIpcService : IDisposable
{
    private readonly List<Action> disposeActions = [];

    public AdsIpcService(
        IDalamudPluginInterface pluginInterface,
        Func<bool> startDutyFromOutside,
        Func<bool> startDutyFromInside,
        Func<bool> resumeDutyFromInside,
        Func<bool> leaveDuty,
        Func<bool> openLootUi,
        Func<bool> toggleLootUi,
        Func<string, bool> startRepair,
        Func<bool> startExtractMateria,
        Func<string, bool> startDesynth,
        Func<bool> cancelUtility,
        Func<bool> openDesynthConfigUi,
        Func<string> getStatusJson,
        Func<string> getCurrentAnalysisJson,
        Func<string> getCapabilitiesJson,
        Func<string, string, string> invoke,
        Func<string> getConfigurationJson,
        Func<string, string> patchConfigurationJson,
        Func<string> getDesynthStatusJson,
        Func<string> getExtractMateriaStatusJson)
    {
        Register(pluginInterface, "ADS.StartDutyFromOutside", startDutyFromOutside);
        Register(pluginInterface, "ADS.StartDutyFromInside", startDutyFromInside);
        Register(pluginInterface, "ADS.ResumeDutyFromInside", resumeDutyFromInside);
        Register(pluginInterface, "ADS.LeaveDuty", leaveDuty);
        Register(pluginInterface, "ADS.OpenLootUi", openLootUi);
        Register(pluginInterface, "ADS.ToggleLootUi", toggleLootUi);
        Register(pluginInterface, "ADS.StartRepair", startRepair);
        Register(pluginInterface, "ADS.StartExtractMateria", startExtractMateria);
        Register(pluginInterface, "ADS.StartDesynth", startDesynth);
        Register(pluginInterface, "ADS.CancelUtility", cancelUtility);
        Register(pluginInterface, "ADS.OpenDesynthConfigUi", openDesynthConfigUi);
        Register(pluginInterface, "ADS.GetStatusJson", getStatusJson);
        Register(pluginInterface, "ADS.GetCurrentAnalysisJson", getCurrentAnalysisJson);
        Register(pluginInterface, "ADS.GetCapabilitiesJson", getCapabilitiesJson);
        Register(pluginInterface, "ADS.Invoke", invoke);
        Register(pluginInterface, "ADS.GetConfigurationJson", getConfigurationJson);
        Register(pluginInterface, "ADS.PatchConfigurationJson", patchConfigurationJson);
        Register(pluginInterface, "ADS.GetDesynthStatusJson", getDesynthStatusJson);
        Register(pluginInterface, "ADS.GetExtractMateriaStatusJson", getExtractMateriaStatusJson);
    }

    public void Dispose()
    {
        foreach (var action in disposeActions)
            action();
    }

    private void Register<TReturn>(IDalamudPluginInterface pluginInterface, string name, Func<TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<T, TReturn>(IDalamudPluginInterface pluginInterface, string name, Func<T, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<T, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }

    private void Register<T1, T2, TReturn>(IDalamudPluginInterface pluginInterface, string name, Func<T1, T2, TReturn> func)
    {
        var provider = pluginInterface.GetIpcProvider<T1, T2, TReturn>(name);
        provider.RegisterFunc(func);
        disposeActions.Add(provider.UnregisterFunc);
    }
}
