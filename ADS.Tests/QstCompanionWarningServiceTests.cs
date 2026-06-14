using ADS.Services;

namespace ADS.Tests;

public sealed class QstCompanionWarningServiceTests
{
    [Fact]
    public void LoadedCompanionShowsOneExactNormalToastPerTerritoryChange()
    {
        var toasts = new List<string>();
        var service = CreateService(isLoaded: () => true, showNormalToast: toasts.Add);

        service.HandleTerritoryChanged();
        service.HandleTerritoryChanged();

        Assert.Equal(
            [
                "You have Questionable Companion enabled, it will cause you to disband from party on return and other cases.",
                "You have Questionable Companion enabled, it will cause you to disband from party on return and other cases.",
            ],
            toasts);
    }

    [Fact]
    public void UnloadedCompanionShowsNoToast()
    {
        var toasts = new List<string>();
        var service = CreateService(isLoaded: () => false, showNormalToast: toasts.Add);

        service.HandleTerritoryChanged();

        Assert.Empty(toasts);
    }

    [Fact]
    public void ToastFailureIsCaughtAndLogged()
    {
        var warnings = new List<string>();
        var service = CreateService(
            isLoaded: () => true,
            showNormalToast: _ => throw new InvalidOperationException("toast unavailable"),
            logWarning: warnings.Add);

        service.HandleTerritoryChanged();

        Assert.Single(warnings);
        Assert.Contains("Toast failed: toast unavailable", warnings[0]);
    }

    [Fact]
    public void DisableUsesExactQstCompanionCommand()
    {
        var commands = new List<string>();
        var service = CreateService(processCommand: command =>
        {
            commands.Add(command);
            return true;
        });

        Assert.True(service.Disable());
        Assert.Equal(["/xldisableplugin QSTCompanion"], commands);
    }

    private static QstCompanionWarningService CreateService(
        Func<bool>? isLoaded = null,
        Action<string>? showNormalToast = null,
        Func<string, bool>? processCommand = null,
        Action<string>? logWarning = null)
        => new(
            isLoaded ?? (() => false),
            showNormalToast ?? (_ => { }),
            processCommand ?? (_ => false),
            logWarning ?? (_ => { }));
}
