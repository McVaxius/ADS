using System.Security.Cryptography;
using System.Text;

namespace ADS.Services;

internal readonly record struct BundledConfigSyncResult(
    string PackagedStamp,
    string StatusMessage,
    bool ConfigurationChanged);

internal static class BundledConfigSyncHelper
{
    public static BundledConfigSyncResult EnsureCurrent(
        string fileName,
        string configPath,
        string bundledPath,
        string appliedPackagedStamp,
        Action<string> setAppliedPackagedStamp,
        Func<string> getDefaultJson)
    {
        var packagedBytes = GetPackagedBytes(bundledPath, getDefaultJson, out var sourceLabel);
        var packagedStamp = ComputeSha256(packagedBytes);
        var normalizedAppliedStamp = appliedPackagedStamp?.Trim() ?? string.Empty;
        var configurationChanged = false;

        var configDirectory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
            Directory.CreateDirectory(configDirectory);

        if (!File.Exists(configPath))
        {
            File.WriteAllBytes(configPath, packagedBytes);
            configurationChanged = TrySetAppliedStamp(packagedStamp, normalizedAppliedStamp, setAppliedPackagedStamp);
            return new BundledConfigSyncResult(
                packagedStamp,
                $"Seeded {fileName} into the plugin config directory from {sourceLabel}.",
                configurationChanged);
        }

        var configBytes = File.ReadAllBytes(configPath);
        var configStamp = ComputeSha256(configBytes);
        if (string.Equals(configStamp, packagedStamp, StringComparison.Ordinal))
        {
            configurationChanged = TrySetAppliedStamp(packagedStamp, normalizedAppliedStamp, setAppliedPackagedStamp);
            var status = configurationChanged
                ? $"{fileName} already matched the packaged copy; recorded the applied packaged stamp."
                : $"{fileName} unchanged; config copy already matches the packaged copy.";
            return new BundledConfigSyncResult(packagedStamp, status, configurationChanged);
        }

        if (string.Equals(normalizedAppliedStamp, packagedStamp, StringComparison.Ordinal))
        {
            return new BundledConfigSyncResult(
                packagedStamp,
                $"{fileName} unchanged; packaged copy has not changed since the last applied version, so ADS kept the local config copy.",
                configurationChanged);
        }

        var backupPath = BackupExistingConfig(configPath, fileName);
        File.WriteAllBytes(configPath, packagedBytes);
        configurationChanged = TrySetAppliedStamp(packagedStamp, normalizedAppliedStamp, setAppliedPackagedStamp);
        return new BundledConfigSyncResult(
            packagedStamp,
            $"Backed up {fileName} to {backupPath} and overwrote the config copy from {sourceLabel}.",
            configurationChanged);
    }

    private static byte[] GetPackagedBytes(string bundledPath, Func<string> getDefaultJson, out string sourceLabel)
    {
        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            sourceLabel = $"the packaged file at {bundledPath}";
            return File.ReadAllBytes(bundledPath);
        }

        sourceLabel = "the built-in defaults";
        return Encoding.UTF8.GetBytes(getDefaultJson());
    }

    private static string BackupExistingConfig(string configPath, string fileName)
    {
        var configDirectory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException($"Could not resolve the config directory for {fileName}.");
        var backupDirectory = Path.Combine(configDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var backupName = $"{Path.GetFileNameWithoutExtension(fileName)}-{timestamp}{Path.GetExtension(fileName)}";
        var backupPath = Path.Combine(backupDirectory, backupName);
        File.Copy(configPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool TrySetAppliedStamp(string packagedStamp, string appliedPackagedStamp, Action<string> setAppliedPackagedStamp)
    {
        if (string.Equals(packagedStamp, appliedPackagedStamp, StringComparison.Ordinal))
            return false;

        setAppliedPackagedStamp(packagedStamp);
        return true;
    }
}
