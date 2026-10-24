using System.Globalization;
using System.Reflection;
using ADS.Services;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class BmrMaxLoadDistanceIpcTests
{
    [Fact]
    public void LoadDistanceUsesInvariantSavedConfigIpcAndChecksTheBoundLiveValue()
    {
        var f = new Fixture();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(BmrReflectionService.ApplyMaxLoadDistanceViaIpc(f.Interface, f.Config, 123.5f));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal(new[] { "NativeConfig", "MaxLoadDistance", "123.5" }, f.Arguments);
        Assert.True(f.Save);
        Assert.Equal(123.5f, f.Config.MaxLoadDistance);
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unreadable")]
    [InlineData("ignored")]
    [InlineData("throws")]
    public void AttemptedFailureDoesNotPermitASecondWrite(string failure)
    {
        var f = new Fixture { Failure = failure };
        if (failure == "throws")
            Assert.Throws<InvalidOperationException>(() => BmrReflectionService.ApplyMaxLoadDistanceViaIpc(f.Interface, f.Config, 123.5f));
        else
            Assert.False(BmrReflectionService.ApplyMaxLoadDistanceViaIpc(f.Interface, f.Config, 123.5f));
        Assert.Equal(1, f.Writes);
        Assert.Equal(500f, f.Config.MaxLoadDistance);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("wrong-provider")]
    public void UnavailableOrAmbiguousProviderDoesNotDispatch(string state)
    {
        var f = new Fixture { Available = state != "missing" };
        if (state == "ambiguous") f.Plugins.Add(Plugin("BossMod"));
        if (state == "wrong-provider") { f.Plugins.Clear(); f.Plugins.Add(Plugin("BossMod")); }
        Assert.Null(BmrReflectionService.ApplyMaxLoadDistanceViaIpc(f.Interface, f.Config, 123.5f));
        Assert.Equal(0, f.Writes);
    }

    private sealed class NativeConfig { public float MaxLoadDistance = 500; }
    private sealed class Fixture
    {
        internal readonly IDalamudPluginInterface Interface;
        internal readonly List<IExposedPlugin> Plugins = new() { Plugin("BossModReborn") };
        internal readonly NativeConfig Config = new();
        internal bool Available = true, Save;
        internal string? Failure;
        internal int Writes;
        internal string[]? Arguments;

        internal Fixture()
        {
            Interface = (IDalamudPluginInterface)Proxy(typeof(IDalamudPluginInterface), (method, args) =>
            {
                if (method.Name == "get_InstalledPlugins") return Plugins;
                Assert.Equal("GetIpcSubscriber", method.Name);
                Assert.Equal("BossMod.Configuration", args![0]);
                Assert.Equal(typeof(List<string>), method.GetGenericArguments()[0]);
                return Proxy(method.ReturnType, (call, values) =>
                {
                    if (call.Name == "get_HasFunction") return Available;
                    Assert.Equal("InvokeFunc", call.Name);
                    Writes++;
                    Arguments = ((List<string>)values![0]!).ToArray();
                    Save = (bool)values[1]!;
                    if (Failure == "throws") throw new InvalidOperationException("Synthetic uncertain write.");
                    if (Failure == null) Config.MaxLoadDistance = float.Parse(Arguments[2], CultureInfo.InvariantCulture);
                    return Failure == "unreadable" ? null : Failure == "rejected" ? new List<string> { "Synthetic rejection" } : new List<string>();
                });
            });
        }
    }

    private static IExposedPlugin Plugin(string name) => (IExposedPlugin)Proxy(typeof(IExposedPlugin), (method, _) => method.Name switch
    {
        "get_InternalName" => name,
        "get_IsLoaded" => true,
        _ => throw new InvalidOperationException(method.Name),
    });

    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create(type, typeof(ContractProxy));
        ((ContractProxy)proxy).Handler = handler;
        return proxy;
    }

    public class ContractProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
