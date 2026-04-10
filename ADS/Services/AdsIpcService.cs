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
        Func<string> getStatusJson,
        Func<string> getCurrentAnalysisJson)
    {
        Register(pluginInterface, "ADS.StartDutyFromOutside", startDutyFromOutside);
        Register(pluginInterface, "ADS.StartDutyFromInside", startDutyFromInside);
        Register(pluginInterface, "ADS.ResumeDutyFromInside", resumeDutyFromInside);
        Register(pluginInterface, "ADS.LeaveDuty", leaveDuty);
        Register(pluginInterface, "ADS.GetStatusJson", getStatusJson);
        Register(pluginInterface, "ADS.GetCurrentAnalysisJson", getCurrentAnalysisJson);
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
}
