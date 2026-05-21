using Dalamud.Plugin;

namespace ADS.Services;

public sealed class ReflectionIpcService : IDisposable
{
    private readonly List<Action> disposeActions = [];

    public ReflectionIpcService(IDalamudPluginInterface pluginInterface, BmrReflectionService reflectionService)
    {
        Register(pluginInterface, "ADS.Reflection.BMR.GetStatusJson", reflectionService.GetStatusJson);
        Register<bool, bool>(pluginInterface, "ADS.Reflection.BMR.SetQueenLunatenderDisabled", disabled => reflectionService.RequestQueenLunatenderDisabled(disabled));
        Register(pluginInterface, "ADS.Reflection.BMR.GetQueenLunatenderDisabled", () => reflectionService.QueenLunatenderDisabled);
        Register<bool, bool>(pluginInterface, "ADS.Reflection.BMR.SetHuntsDisabled", disabled => reflectionService.RequestHuntsDisabled(disabled));
        Register(pluginInterface, "ADS.Reflection.BMR.GetHuntsDisabled", () => reflectionService.HuntsDisabled);
        Register<float, bool>(pluginInterface, "ADS.Reflection.BMR.SetMaxLoadDistance", value => reflectionService.RequestMaxLoadDistance(value));
        Register(pluginInterface, "ADS.Reflection.BMR.MinimizeMaxLoadDistance", reflectionService.RequestMinimizeMaxLoadDistance);
        Register(pluginInterface, "ADS.Reflection.BMR.ResetMaxLoadDistance", reflectionService.RequestResetMaxLoadDistance);
        Register(pluginInterface, "ADS.Reflection.BMR.GetMaxLoadDistance", () => reflectionService.CurrentMaxLoadDistance);
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
}
