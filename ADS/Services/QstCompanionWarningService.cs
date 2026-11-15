namespace ADS.Services;

public sealed class QstCompanionWarningService
{
    public const string InternalName = "QSTCompanion";
    public const string DisableCommand = "/xldisableplugin QSTCompanion";
    private readonly Func<string, bool> processCommand;
    private readonly Action<string> logWarning;

    public QstCompanionWarningService(
        Func<string, bool> processCommand,
        Action<string> logWarning)
    {
        this.processCommand = processCommand;
        this.logWarning = logWarning;
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
