using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class XaSlaveSkipperServiceTests
{
    [Fact]
    public void TextAdvanceEnabledLeavesXaSlaveUnchanged()
    {
        var commands = new List<string>();
        var service = CreateService(textAdvanceEnabled: () => true, commands: commands);

        service.BeginOwnershipRun();
        service.HandleManualCommand(string.Empty);
        service.HandleManualCommand("on");
        service.HandleManualCommand("off");

        Assert.Empty(commands);
        Assert.False(service.FallbackEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledOrUnavailableTextAdvanceEnablesExactXaSlavePair(bool throwOnTextAdvanceQuery)
    {
        var commands = new List<string>();
        var service = CreateService(
            textAdvanceEnabled: () => throwOnTextAdvanceQuery
                ? throw new InvalidOperationException("TextAdvance unavailable")
                : false,
            commands: commands);

        service.BeginOwnershipRun();

        Assert.Equal(
            [XaSlaveSkipperService.EnableDialogueCommand, XaSlaveSkipperService.EnableCutscenesCommand],
            commands);
        Assert.True(service.FallbackEnabled);
    }

    [Fact]
    public void UnavailableXaSlaveDoesNotDispatchOrShowSuccessToast()
    {
        var commands = new List<string>();
        var toasts = new List<string>();
        var service = CreateService(xaSlaveAvailable: () => false, commands: commands, toasts: toasts);

        var result = service.BeginOwnershipRun();

        Assert.True(result.FallbackUnavailable);
        Assert.Empty(commands);
        Assert.Empty(toasts);
        Assert.False(service.FallbackEnabled);
    }

    [Fact]
    public void OwnershipTransitionsDispatchOnceAndKeepFallbackEnabledWhileLeaving()
    {
        var commands = new List<string>();
        var service = CreateService(commands: commands);

        service.BeginOwnershipRun();
        service.BeginOwnershipRun();
        service.Synchronize(OwnershipMode.OwnedStartInside);
        service.Synchronize(OwnershipMode.Leaving);
        service.Synchronize(OwnershipMode.Idle);
        service.Synchronize(OwnershipMode.Idle);

        Assert.Equal(
        [
            XaSlaveSkipperService.EnableDialogueCommand,
            XaSlaveSkipperService.EnableCutscenesCommand,
            XaSlaveSkipperService.DisableDialogueCommand,
            XaSlaveSkipperService.DisableCutscenesCommand,
        ], commands);
    }

    [Fact]
    public void ManualToggleOnAndOffAreCurrentRunOnlyAndToastOncePerSuccessfulChange()
    {
        var commands = new List<string>();
        var toasts = new List<string>();
        var service = CreateService(commands: commands, toasts: toasts);

        var idle = service.HandleManualCommand(string.Empty);
        service.BeginOwnershipRun();
        service.HandleManualCommand(string.Empty);
        service.HandleManualCommand(string.Empty);
        service.HandleManualCommand("on");
        service.HandleManualCommand("off");
        service.HandleManualCommand("off");
        service.Synchronize(OwnershipMode.Idle);
        service.BeginOwnershipRun();

        Assert.Equal("There is no active ADS ownership run to control.", idle.Status);
        Assert.Equal(
            [XaSlaveSkipperService.DisabledToast, XaSlaveSkipperService.EnabledToast, XaSlaveSkipperService.DisabledToast],
            toasts);
        Assert.Equal(10, commands.Count);
        Assert.Equal(
            [XaSlaveSkipperService.EnableDialogueCommand, XaSlaveSkipperService.EnableCutscenesCommand],
            commands.Skip(commands.Count - 2).ToArray());
    }

    [Fact]
    public void ManualOffSuppressesFallbackUntilTheRunEnds()
    {
        var commands = new List<string>();
        var service = CreateService(commands: commands);

        service.BeginOwnershipRun();
        service.HandleManualCommand("off");
        service.Synchronize(OwnershipMode.OwnedStartInside);
        service.Synchronize(OwnershipMode.Idle);
        service.BeginOwnershipRun();

        Assert.Equal(
        [
            XaSlaveSkipperService.EnableDialogueCommand,
            XaSlaveSkipperService.EnableCutscenesCommand,
            XaSlaveSkipperService.DisableDialogueCommand,
            XaSlaveSkipperService.DisableCutscenesCommand,
            XaSlaveSkipperService.EnableDialogueCommand,
            XaSlaveSkipperService.EnableCutscenesCommand,
        ], commands);
    }

    private static XaSlaveSkipperService CreateService(
        Func<bool>? textAdvanceEnabled = null,
        Func<bool>? xaSlaveAvailable = null,
        List<string>? commands = null,
        List<string>? toasts = null)
    {
        commands ??= [];
        toasts ??= [];
        return new XaSlaveSkipperService(
            textAdvanceEnabled ?? (() => false),
            xaSlaveAvailable ?? (() => true),
            command =>
            {
                commands.Add(command);
                return true;
            },
            toasts.Add);
    }
}
