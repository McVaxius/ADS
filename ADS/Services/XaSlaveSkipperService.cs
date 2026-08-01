using ADS.Models;

namespace ADS.Services;

internal readonly record struct XaSlaveSkipperResult(
    string Status,
    bool StateChanged = false,
    bool FallbackUnavailable = false);

internal sealed class XaSlaveSkipperService
{
    internal const string EnableDialogueCommand = "/xa skipdialogue on";
    internal const string EnableCutscenesCommand = "/xa skipcutscenes on";
    internal const string DisableDialogueCommand = "/xa skipdialogue off";
    internal const string DisableCutscenesCommand = "/xa skipcutscenes off";
    internal const string EnabledToast = "AIDS Advanced ON";
    internal const string DisabledToast = "AIDS Advanced OFF";

    private readonly Func<bool> isTextAdvanceEnabled;
    private readonly Func<bool> isXaSlaveAvailable;
    private readonly Func<string, bool> processCommand;
    private readonly Action<string> showNormalToast;
    private readonly Action<string>? logWarning;
    private bool ownershipRunActive;
    private bool fallbackEnabled;
    private bool xaSlaveStateMayBeEnabled;
    private bool suppressedForRun;

    public XaSlaveSkipperService(
        Func<bool> isTextAdvanceEnabled,
        Func<bool> isXaSlaveAvailable,
        Func<string, bool> processCommand,
        Action<string> showNormalToast,
        Action<string>? logWarning = null)
    {
        this.isTextAdvanceEnabled = isTextAdvanceEnabled;
        this.isXaSlaveAvailable = isXaSlaveAvailable;
        this.processCommand = processCommand;
        this.showNormalToast = showNormalToast;
        this.logWarning = logWarning;
    }

    public bool OwnershipRunActive => ownershipRunActive;
    public bool FallbackEnabled => fallbackEnabled;

    public XaSlaveSkipperResult BeginOwnershipRun()
    {
        if (ownershipRunActive)
            return new XaSlaveSkipperResult("XA Slave skipper fallback is already synchronized for this ADS run.");

        ownershipRunActive = true;
        fallbackEnabled = false;
        xaSlaveStateMayBeEnabled = false;
        suppressedForRun = false;

        return IsTextAdvanceEnabled()
            ? new XaSlaveSkipperResult("TextAdvance is enabled; ADS did not use the XA Slave skipper fallback.")
            : EnableFallback(showToast: false);
    }

    public XaSlaveSkipperResult Synchronize(OwnershipMode ownershipMode)
    {
        if (!ownershipRunActive || IsOwnershipRunActive(ownershipMode))
            return new XaSlaveSkipperResult(string.Empty);

        var result = xaSlaveStateMayBeEnabled
            ? DispatchFallback(enabled: false, showToast: false)
            : new XaSlaveSkipperResult("XA Slave skipper fallback was not enabled for this ADS run.");
        ResetRun();
        return result;
    }

    public XaSlaveSkipperResult HandleManualCommand(string argument)
    {
        if (!ownershipRunActive)
            return new XaSlaveSkipperResult("There is no active ADS ownership run to control.");

        var normalized = argument.Trim();
        if (normalized.Length != 0
            && !normalized.Equals("on", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return new XaSlaveSkipperResult("Skipper must be: /ads skipper [on|off].");
        }

        var textAdvanceEnabled = IsTextAdvanceEnabled();
        if (normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            suppressedForRun = true;
            return textAdvanceEnabled
                ? new XaSlaveSkipperResult("TextAdvance is enabled; ADS suppressed its XA Slave skipper fallback for this run.")
                : DisableFallback(showToast: true);
        }

        if (normalized.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            suppressedForRun = false;
            return textAdvanceEnabled
                ? new XaSlaveSkipperResult("TextAdvance is enabled; ADS did not change the XA Slave skipper fallback.")
                : EnableFallback(showToast: true);
        }

        if (textAdvanceEnabled)
            return new XaSlaveSkipperResult("TextAdvance is enabled; ADS did not change the XA Slave skipper fallback.");

        if (fallbackEnabled)
        {
            suppressedForRun = true;
            return DisableFallback(showToast: true);
        }

        suppressedForRun = false;
        return EnableFallback(showToast: true);
    }

    public XaSlaveSkipperResult EndOwnershipRun()
        => Synchronize(OwnershipMode.Idle);

    private XaSlaveSkipperResult EnableFallback(bool showToast)
    {
        if (suppressedForRun)
            return new XaSlaveSkipperResult("XA Slave skipper fallback is suppressed until this ADS ownership run ends.");

        if (fallbackEnabled)
            return new XaSlaveSkipperResult("XA Slave skipper fallback is already enabled for this ADS run.");

        return DispatchFallback(enabled: true, showToast);
    }

    private XaSlaveSkipperResult DisableFallback(bool showToast)
    {
        if (!xaSlaveStateMayBeEnabled)
            return new XaSlaveSkipperResult("XA Slave skipper fallback is already off for this ADS run.");

        return DispatchFallback(enabled: false, showToast);
    }

    private XaSlaveSkipperResult DispatchFallback(bool enabled, bool showToast)
    {
        if (!IsXaSlaveAvailable())
        {
            return new XaSlaveSkipperResult(
                "XA Slave skipper fallback is unavailable; no skipper commands were sent.",
                FallbackUnavailable: true);
        }

        var dialogueSent = ProcessCommand(enabled ? EnableDialogueCommand : DisableDialogueCommand);
        var cutscenesSent = ProcessCommand(enabled ? EnableCutscenesCommand : DisableCutscenesCommand);
        if (!dialogueSent || !cutscenesSent)
        {
            if (enabled && (dialogueSent || cutscenesSent))
                xaSlaveStateMayBeEnabled = true;

            return new XaSlaveSkipperResult("XA Slave skipper fallback did not accept both skipper commands.");
        }

        fallbackEnabled = enabled;
        xaSlaveStateMayBeEnabled = enabled;
        if (showToast)
            ShowToast(enabled ? EnabledToast : DisabledToast);

        return new XaSlaveSkipperResult(
            enabled
                ? "XA Slave skipper fallback enabled for this ADS run."
                : "XA Slave skipper fallback disabled for this ADS run.",
            StateChanged: true);
    }

    private bool IsTextAdvanceEnabled()
    {
        try
        {
            return isTextAdvanceEnabled();
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"[ADS][Skipper] TextAdvance.IsEnabled query failed: {ex.Message}");
            return false;
        }
    }

    private bool IsXaSlaveAvailable()
    {
        try
        {
            return isXaSlaveAvailable();
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"[ADS][Skipper] XA Slave availability check failed: {ex.Message}");
            return false;
        }
    }

    private bool ProcessCommand(string command)
    {
        try
        {
            return processCommand(command);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"[ADS][Skipper] Command failed ({command}): {ex.Message}");
            return false;
        }
    }

    private void ShowToast(string message)
    {
        try
        {
            showNormalToast(message);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"[ADS][Skipper] Toast failed: {ex.Message}");
        }
    }

    private void ResetRun()
    {
        ownershipRunActive = false;
        fallbackEnabled = false;
        xaSlaveStateMayBeEnabled = false;
        suppressedForRun = false;
    }

    private static bool IsOwnershipRunActive(OwnershipMode ownershipMode)
        => ownershipMode is OwnershipMode.OwnedStartOutside
            or OwnershipMode.OwnedStartInside
            or OwnershipMode.OwnedResumeInside
            or OwnershipMode.Leaving;
}
