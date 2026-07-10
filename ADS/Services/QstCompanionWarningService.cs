namespace ADS.Services;

public sealed class QstCompanionWarningService
{
    public const string InternalName = "QSTCompanion";
    public const string DisableCommand = "/xldisableplugin QSTCompanion";
    public const string WarningMessage =
//        "You have Questionable Companion enabled, it will cause you to disband from party on return and other cases.";
        "Stay hydrated and make sure to stretch your limbs.  You're working very hard.";

    private readonly Func<bool> isLoaded;
    private readonly Action<string> showNormalToast;
    private readonly Func<string, bool> processCommand;
    private readonly Action<string> logWarning;

    public QstCompanionWarningService(
        Func<bool> isLoaded,
        Action<string> showNormalToast,
        Func<string, bool> processCommand,
        Action<string> logWarning)
    {
        this.isLoaded = isLoaded;
        this.showNormalToast = showNormalToast;
        this.processCommand = processCommand;
        this.logWarning = logWarning;
    }

    public void HandleTerritoryChanged()
    {
        bool loaded;
        try
        {
            loaded = isLoaded();
        }
        catch (Exception ex)
        {
            logWarning($"[ADS][QSTCompanion] Installed-plugin check failed: {ex.Message}");
            return;
        }

        if (!loaded)
            return;

        try
        {
            showNormalToast(WarningMessage);
        }
        catch (Exception ex)
        {
            logWarning($"[ADS][QSTCompanion] Toast failed: {ex.Message}");
        }
    }

    public bool Disable()
    {
        try
        {
            return processCommand(DisableCommand);
        }
        catch (Exception ex)
        {
            logWarning($"[ADS][QSTCompanion] Disable command failed: {ex.Message}");
            return false;
        }
    }
}
