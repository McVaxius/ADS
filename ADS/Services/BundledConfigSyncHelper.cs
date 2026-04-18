namespace ADS.Services;

internal sealed record BundledRuleSyncResult(
    string ObjectRulesStatus,
    string DialogRulesStatus,
    bool ConfigurationChanged);

internal static class BundledConfigSyncHelper
{
    public const string ObjectRulesFileName = "duty-object-rules.json";
    public const string DialogRulesFileName = "dialog-yesno-rules.json";

    public static BundledRuleSyncResult SyncBundledRulesIfPluginVersionAdvanced(
        string currentPluginVersion,
        string lastSyncedPluginVersion,
        string configDirectory,
        string? assemblyDirectory,
        Action<string> setInstalledRuleSyncVersion)
    {
        Directory.CreateDirectory(configDirectory);
        if (!ShouldRefreshBundledRules(currentPluginVersion, lastSyncedPluginVersion, out var refreshReason))
        {
            var skippedStatus = $"Skipped packaged rule refresh: {refreshReason}.";
            return new BundledRuleSyncResult(skippedStatus, skippedStatus, false);
        }

        var objectSync = TryOverwriteRuleFile(ObjectRulesFileName, configDirectory, assemblyDirectory, refreshReason);
        var dialogSync = TryOverwriteRuleFile(DialogRulesFileName, configDirectory, assemblyDirectory, refreshReason);
        if (objectSync.Success && dialogSync.Success)
        {
            var normalizedCurrentVersion = currentPluginVersion.Trim();
            var configurationChanged = !string.Equals(normalizedCurrentVersion, lastSyncedPluginVersion?.Trim() ?? string.Empty, StringComparison.Ordinal);
            if (configurationChanged)
                setInstalledRuleSyncVersion(normalizedCurrentVersion);

            return new BundledRuleSyncResult(
                $"{objectSync.StatusMessage} Stored synced plugin version {normalizedCurrentVersion} in ADS config.",
                $"{dialogSync.StatusMessage} Stored synced plugin version {normalizedCurrentVersion} in ADS config.",
                configurationChanged);
        }

        const string configNotAdvancedSuffix = " ADS did not advance InstalledRuleSyncVersion because not all packaged rule files refreshed successfully.";
        return new BundledRuleSyncResult(
            objectSync.StatusMessage + configNotAdvancedSuffix,
            dialogSync.StatusMessage + configNotAdvancedSuffix,
            false);
    }

    private static bool ShouldRefreshBundledRules(string currentPluginVersion, string lastSyncedPluginVersion, out string reason)
    {
        var normalizedCurrentVersion = currentPluginVersion?.Trim() ?? string.Empty;
        if (!Version.TryParse(normalizedCurrentVersion, out var currentVersion))
        {
            reason = $"current ADS plugin version '{normalizedCurrentVersion}' is not a valid semantic version";
            return false;
        }

        var normalizedLastSyncedVersion = lastSyncedPluginVersion?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedLastSyncedVersion))
        {
            reason = $"no prior InstalledRuleSyncVersion was recorded, so ADS {normalizedCurrentVersion} must seed the packaged defaults";
            return true;
        }

        if (!Version.TryParse(normalizedLastSyncedVersion, out var storedVersion))
        {
            reason = $"stored InstalledRuleSyncVersion '{normalizedLastSyncedVersion}' is invalid, so ADS {normalizedCurrentVersion} must refresh the packaged defaults";
            return true;
        }

        if (currentVersion > storedVersion)
        {
            reason = $"ADS {normalizedCurrentVersion} is newer than the last synced version {normalizedLastSyncedVersion}";
            return true;
        }

        reason = $"ADS {normalizedCurrentVersion} is not newer than the last synced version {normalizedLastSyncedVersion}";
        return false;
    }

    private static (bool Success, string StatusMessage) TryOverwriteRuleFile(
        string fileName,
        string configDirectory,
        string? assemblyDirectory,
        string refreshReason)
    {
        var targetPath = Path.Combine(configDirectory, fileName);
        var sourcePath = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? string.Empty
            : Path.Combine(assemblyDirectory, fileName);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return (false, $"Failed to refresh {fileName}: packaged file was not found at {sourcePath}.");
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
        return (true, $"Overwrote {targetPath} from packaged file {sourcePath} because {refreshReason}.");
    }
}
