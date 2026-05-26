using System.Collections;
using System.Globalization;
using System.Reflection;
using ADS.Models;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class BossModMultiboxFollowService
{
    private const string MultiboxModuleFullName = "BossMod.Autorotation.MiscAI.Multibox";
    private const string MultiboxLeaderTrack = "Leader";

    private static readonly TimeSpan SuccessReapplyInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] MultiboxModuleTypeNames =
    [
        "BossMod.Autorotation.MiscAI.Multibox, BossMod",
        "BossMod.Autorotation.MiscAI.Multibox, BossModReborn",
        "BossMod.Autorotation.MiscAI.Multibox",
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private DateTime nextApplyUtc = DateTime.MinValue;
    private ulong? pendingLeaderContentId;
    private string pendingOpenerName = string.Empty;
    private string lastLoggedSuccessKey = string.Empty;

    public BossModMultiboxFollowService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool FollowApplied { get; private set; }

    public string FollowStatus { get; private set; } = "No treasure portal opener captured.";

    public ulong? FollowLeaderContentId { get; private set; }

    public string FollowMethod { get; private set; } = string.Empty;

    public void Clear(string reason)
    {
        pendingLeaderContentId = null;
        pendingOpenerName = string.Empty;
        FollowApplied = false;
        FollowLeaderContentId = null;
        FollowMethod = string.Empty;
        FollowStatus = $"Treasure portal follow cleared after {reason}.";
        nextApplyUtc = DateTime.MinValue;
        lastLoggedSuccessKey = string.Empty;
    }

    public void Update(TreasureDungeonRole role, TreasurePortalOpenerSnapshot? opener)
    {
        if (opener is null)
        {
            pendingLeaderContentId = null;
            pendingOpenerName = string.Empty;
            FollowApplied = false;
            FollowLeaderContentId = null;
            FollowMethod = string.Empty;
            FollowStatus = "No treasure portal opener captured.";
            nextApplyUtc = DateTime.MinValue;
            return;
        }

        if (role != TreasureDungeonRole.Follower)
        {
            pendingLeaderContentId = null;
            pendingOpenerName = opener.OpenerName;
            FollowApplied = false;
            FollowLeaderContentId = opener.ContentId;
            FollowMethod = string.Empty;
            FollowStatus = $"Portal opener '{opener.OpenerName}' captured; ADS role is {role}, so BMR/VBM follow is not applied.";
            nextApplyUtc = DateTime.MinValue;
            return;
        }

        if (opener.IsLocalOpener)
        {
            pendingLeaderContentId = null;
            pendingOpenerName = opener.OpenerName;
            FollowApplied = false;
            FollowLeaderContentId = opener.ContentId;
            FollowMethod = string.Empty;
            FollowStatus = $"Portal opener '{opener.OpenerName}' is local player; BMR/VBM follow target not applied.";
            nextApplyUtc = DateTime.MinValue;
            return;
        }

        if (opener.ContentId is not { } leaderContentId)
        {
            pendingLeaderContentId = null;
            pendingOpenerName = opener.OpenerName;
            FollowApplied = false;
            FollowLeaderContentId = null;
            FollowMethod = string.Empty;
            FollowStatus = $"Portal opener '{opener.OpenerName}' captured, but content id is not resolved yet; retrying.";
            nextApplyUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (pendingLeaderContentId == leaderContentId
            && string.Equals(pendingOpenerName, opener.OpenerName, StringComparison.Ordinal)
            && now < nextApplyUtc)
        {
            return;
        }

        pendingLeaderContentId = leaderContentId;
        pendingOpenerName = opener.OpenerName;
        FollowLeaderContentId = leaderContentId;

        if (TryApplyViaIpc(leaderContentId, out var ipcStatus))
        {
            MarkApplied("IPC", leaderContentId, opener.OpenerName, ipcStatus);
            return;
        }

        if (TryApplyViaReflection(leaderContentId, out var reflectionStatus))
        {
            MarkApplied("Reflection", leaderContentId, opener.OpenerName, reflectionStatus);
            return;
        }

        FollowApplied = false;
        FollowMethod = string.Empty;
        FollowStatus = $"{ipcStatus} Reflection fallback failed: {reflectionStatus}";
        nextApplyUtc = now + FailureRetryInterval;
    }

    private void MarkApplied(string method, ulong leaderContentId, string openerName, string detail)
    {
        FollowApplied = true;
        FollowMethod = method;
        FollowLeaderContentId = leaderContentId;
        FollowStatus = $"BMR/VBM Multibox Leader set to portal opener '{openerName}' content id {leaderContentId.ToString(CultureInfo.InvariantCulture)} via {method}. {detail}";
        nextApplyUtc = DateTime.UtcNow + SuccessReapplyInterval;

        var successKey = $"{method}:{leaderContentId}:{detail}";
        if (string.Equals(successKey, lastLoggedSuccessKey, StringComparison.Ordinal))
            return;

        lastLoggedSuccessKey = successKey;
        log.Information($"[ADS] {FollowStatus}");
    }

    private bool TryApplyViaIpc(ulong leaderContentId, out string status)
    {
        try
        {
            var activePresets = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList").InvokeFunc();
            if (activePresets.Count == 0)
            {
                status = "BossMod IPC returned no active autorotation presets.";
                return false;
            }

            var addTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
            var appliedPresets = new List<string>();
            var skippedPresets = new List<string>();
            var value = leaderContentId.ToString(CultureInfo.InvariantCulture);
            foreach (var presetName in activePresets)
            {
                var applied = false;
                foreach (var moduleTypeName in MultiboxModuleTypeNames)
                {
                    if (!addTransientStrategy.InvokeFunc(presetName, moduleTypeName, MultiboxLeaderTrack, value))
                        continue;

                    applied = true;
                    break;
                }

                if (applied)
                    appliedPresets.Add(presetName);
                else
                    skippedPresets.Add(presetName);
            }

            if (appliedPresets.Count == 0)
            {
                status = $"BossMod IPC found active preset(s) [{string.Join(", ", activePresets)}], but none contained {MultiboxModuleFullName}.{MultiboxLeaderTrack}.";
                return false;
            }

            status = skippedPresets.Count == 0
                ? $"Applied to active preset(s): {string.Join(", ", appliedPresets)}."
                : $"Applied to active preset(s): {string.Join(", ", appliedPresets)}. Skipped preset(s) without Multibox: {string.Join(", ", skippedPresets)}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"BossMod IPC unavailable or failed: {ex.Message}";
            return false;
        }
    }

    private bool TryApplyViaReflection(ulong leaderContentId, out string status)
    {
        status = string.Empty;
        try
        {
            var exposedPlugin = FindBossModPlugin();
            if (exposedPlugin is null)
            {
                status = "BossMod/VBM plugin not found.";
                return false;
            }

            if (!exposedPlugin.IsLoaded)
            {
                status = "BossMod/VBM plugin is installed but not loaded.";
                return false;
            }

            var localPlugin = GetLocalPlugin(exposedPlugin);
            if (localPlugin is null)
            {
                status = "Could not reflect Dalamud LocalPlugin from BossMod/VBM.";
                return false;
            }

            var instance = GetFieldValue(localPlugin, "instance");
            if (instance is null)
            {
                status = "BossMod/VBM plugin instance not available yet.";
                return false;
            }

            var rotation = GetFieldValue(instance, "_rotation");
            if (rotation is null)
            {
                status = "BossMod/VBM _rotation manager not initialized yet.";
                return false;
            }

            var presets = GetMemberValue(rotation, "Presets") as IEnumerable;
            if (presets is null)
            {
                status = "BossMod/VBM active preset list not available.";
                return false;
            }

            var activePresetNames = new List<string>();
            var appliedPresetNames = new List<string>();
            foreach (var preset in presets)
            {
                if (preset is null)
                    continue;

                var presetName = GetMemberValue(preset, "Name")?.ToString() ?? "<unnamed>";
                activePresetNames.Add(presetName);
                if (TryApplyToPresetByReflection(preset, leaderContentId))
                    appliedPresetNames.Add(presetName);
            }

            if (activePresetNames.Count == 0)
            {
                status = "BossMod/VBM has no active autorotation presets.";
                return false;
            }

            if (appliedPresetNames.Count == 0)
            {
                status = $"BossMod/VBM active preset(s) [{string.Join(", ", activePresetNames)}] did not contain {MultiboxModuleFullName}.";
                return false;
            }

            status = $"Applied to active preset(s): {string.Join(", ", appliedPresetNames)}.";
            return true;
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return false;
        }
    }

    private static bool TryApplyToPresetByReflection(object preset, ulong leaderContentId)
    {
        var modules = GetMemberValue(preset, "Modules") as IEnumerable;
        if (modules is null)
            return false;

        foreach (var moduleSettings in modules)
        {
            if (moduleSettings is null)
                continue;

            var moduleType = GetMemberValue(moduleSettings, "Type") as Type;
            if (moduleType is null
                || !string.Equals(moduleType.FullName, MultiboxModuleFullName, StringComparison.Ordinal))
            {
                continue;
            }

            var transientSettings = GetMemberValue(moduleSettings, "TransientSettings") as IList;
            if (transientSettings is null)
                return false;

            var setting = CreateMultiboxLeaderSetting(moduleType.Assembly, leaderContentId);
            if (setting is null)
                return false;

            var replaced = false;
            for (var i = 0; i < transientSettings.Count; i++)
            {
                var existing = transientSettings[i];
                if (existing is null
                    || Convert.ToInt32(GetMemberValue(existing, "Track"), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                transientSettings[i] = setting;
                replaced = true;
                break;
            }

            if (!replaced)
                transientSettings.Add(setting);

            return true;
        }

        return false;
    }

    private static object? CreateMultiboxLeaderSetting(Assembly assembly, ulong leaderContentId)
    {
        var strategyValueType = assembly.GetType("BossMod.Autorotation.StrategyValueInt");
        var moduleSettingType = assembly.GetType("BossMod.Autorotation.Preset+ModuleSetting");
        var modifierType = assembly.GetType("BossMod.Autorotation.Preset+Modifier");
        if (strategyValueType is null || moduleSettingType is null || modifierType is null)
            return null;

        var strategyValue = Activator.CreateInstance(strategyValueType);
        if (strategyValue is null || !TrySetMember(strategyValue, "Value", unchecked((long)leaderContentId)))
            return null;

        var noModifier = Enum.ToObject(modifierType, 0);
        return Activator.CreateInstance(moduleSettingType, noModifier, 0, strategyValue);
    }

    private IExposedPlugin? FindBossModPlugin()
    {
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (LooksLikeBossMod(plugin))
                return plugin;
        }

        return null;
    }

    private static bool LooksLikeBossMod(IExposedPlugin plugin)
    {
        var name = plugin.Name ?? string.Empty;
        var internalName = plugin.InternalName ?? string.Empty;

        return string.Equals(name, "BossMod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Boss Mod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "BossMod Reborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "BossModReborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossMod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossModReborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossmodReborn", StringComparison.OrdinalIgnoreCase)
               || (name.Contains("BossMod", StringComparison.OrdinalIgnoreCase)
                   && name.Contains("Reborn", StringComparison.OrdinalIgnoreCase));
    }

    private static object? GetLocalPlugin(IExposedPlugin exposedPlugin)
    {
        var type = exposedPlugin.GetType();
        var field = FindField(type, "<plugin>P")
                    ?? FindField(type, "plugin")
                    ?? FindFields(type).FirstOrDefault(fieldInfo => fieldInfo.FieldType.FullName?.Contains("LocalPlugin", StringComparison.Ordinal) == true);
        return field?.GetValue(exposedPlugin);
    }

    private static object? GetFieldValue(object instance, string name)
        => FindField(instance.GetType(), name)?.GetValue(instance);

    private static object? GetMemberValue(object instance, string name)
    {
        var type = instance.GetType();
        var field = FindField(type, name);
        if (field is not null)
            return field.GetValue(instance);

        var property = FindProperty(type, name);
        return property?.GetValue(instance);
    }

    private static bool TrySetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var field = FindField(type, name);
        if (field is not null)
        {
            field.SetValue(instance, value);
            return true;
        }

        var property = FindProperty(type, name);
        if (property?.CanWrite == true)
        {
            property.SetValue(instance, value);
            return true;
        }

        return false;
    }

    private static FieldInfo? FindField(Type type, string name)
        => FindFields(type).FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static IEnumerable<FieldInfo> FindFields(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property;
        }

        return null;
    }
}
