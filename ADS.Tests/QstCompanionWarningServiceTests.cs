using ADS.Services;

namespace ADS.Tests;

public sealed class QstCompanionWarningServiceTests
{
    [Fact]
    public void DisableFailureIsCaughtAndLogged()
    {
        var warnings = new List<string>();
        var service = CreateService(
            processCommand: _ => throw new InvalidOperationException("command unavailable"),
            logWarning: warnings.Add);

        Assert.False(service.Disable());

        Assert.Single(warnings);
        Assert.Contains("Disable command failed: command unavailable", warnings[0]);
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
        Func<string, bool>? processCommand = null,
        Action<string>? logWarning = null)
        => new(
            processCommand ?? (_ => false),
            logWarning ?? (_ => { }));
}
