using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class BmrReflectionService : IDisposable
{
    public const uint QueenLunatenderOid = 0x35DF;
    public const float DefaultFallbackMaxLoadDistance = 500f;
    public const float DefaultMinimizedMaxLoadDistance = 100f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly object syncRoot = new();
    private readonly Dictionary<uint, object> removedRegistryEntries = [];

    private BmrReflectionStatus lastStatus;
    private object? trackedRegistry;
    private string lastAvailabilityKey = string.Empty;
    private string lastAction = "No reflection action yet.";
    private bool pendingRegistryRestore;
    private bool pendingMaxLoadDistanceRestore;
    private bool clearMaxLoadDistanceDesiredAfterRestore;

    public BmrReflectionService(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.log = log;

        lastStatus = BuildDisabledStatus("Reflection tools disabled.");
    }

    public BmrReflectionStatus Status
    {
        get
        {
            lock (syncRoot)
                return lastStatus;
        }
    }

    public bool QueenLunatenderDisabled
    {
        get
        {
            lock (syncRoot)
                return configuration.ReflectionQueenLunatenderDisabled;
        }
    }

    public bool HuntsDisabled
    {
        get
        {
            lock (syncRoot)
                return configuration.ReflectionHuntsDisabled;
        }
    }

    public float CurrentMaxLoadDistance
    {
        get
        {
            lock (syncRoot)
                return lastStatus.CurrentMaxLoadDistance ?? float.NaN;
        }
    }

    public void Dispose()
    {
    }

    public void Update()
    {
        lock (syncRoot)
        {
            try
            {
                UpdateLocked();
            }
            catch (Exception ex)
            {
                var message = $"Reflection update failed: {ex.Message}";
                lastAction = message;
                SetStatusLocked(BuildUnavailableStatus(null, false, message));
                LogAvailabilityChange(lastStatus);
            }
        }
    }

    public string GetStatusJson()
    {
        lock (syncRoot)
            return JsonSerializer.Serialize(lastStatus, JsonOptions);
    }

    public object CaptureStatusPayload()
    {
        lock (syncRoot)
            return lastStatus;
    }

    public void SetToolsEnabled(bool enabled)
    {
        lock (syncRoot)
        {
            if (configuration.ReflectionToolsEnabled == enabled)
                return;

            configuration.ReflectionToolsEnabled = enabled;
            if (!enabled)
            {
                pendingRegistryRestore = removedRegistryEntries.Count > 0;
                pendingMaxLoadDistanceRestore = configuration.ReflectionHasOriginalMaxLoadDistance;
                clearMaxLoadDistanceDesiredAfterRestore = false;
            }

            configuration.Save();
            lastAction = enabled
                ? "Reflection tools enabled."
                : "Reflection tools disabled; queued in-session restore.";
        }
    }

    public bool RequestQueenLunatenderDisabled(bool disabled)
    {
        lock (syncRoot)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionQueenLunatenderDisabled = disabled;
            configuration.Save();
            lastAction = disabled
                ? "Queued Queen Lunatender disable."
                : "Queued Queen Lunatender restore.";
            return true;
        }
    }

    public bool RequestHuntsDisabled(bool disabled)
    {
        lock (syncRoot)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionHuntsDisabled = disabled;
            configuration.Save();
            lastAction = disabled
                ? "Queued hunt module disable."
                : "Queued hunt module restore.";
            return true;
        }
    }

    public bool RequestMaxLoadDistance(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
            return false;

        lock (syncRoot)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionMinimizedMaxLoadDistance = Math.Clamp(value, 0.1f, DefaultFallbackMaxLoadDistance);
            configuration.ReflectionMaxLoadDistanceMinimized = true;
            configuration.Save();
            lastAction = $"Queued BMR MaxLoadDistance set to {configuration.ReflectionMinimizedMaxLoadDistance.ToString("0.###", CultureInfo.InvariantCulture)}.";
            return true;
        }
    }

    public bool RequestMinimizeMaxLoadDistance()
    {
        lock (syncRoot)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionMaxLoadDistanceMinimized = true;
            if (!float.IsFinite(configuration.ReflectionMinimizedMaxLoadDistance) || configuration.ReflectionMinimizedMaxLoadDistance <= 0f)
                configuration.ReflectionMinimizedMaxLoadDistance = DefaultMinimizedMaxLoadDistance;
            configuration.ReflectionMinimizedMaxLoadDistance = Math.Clamp(configuration.ReflectionMinimizedMaxLoadDistance, 0.1f, DefaultFallbackMaxLoadDistance);
            configuration.Save();
            lastAction = $"Queued BMR MaxLoadDistance minimization to {configuration.ReflectionMinimizedMaxLoadDistance.ToString("0.###", CultureInfo.InvariantCulture)}.";
            return true;
        }
    }

    public bool RequestResetMaxLoadDistance()
    {
        lock (syncRoot)
        {
            configuration.ReflectionToolsEnabled = true;
            configuration.ReflectionMaxLoadDistanceMinimized = false;
            pendingMaxLoadDistanceRestore = true;
            clearMaxLoadDistanceDesiredAfterRestore = true;
            configuration.Save();
            lastAction = "Queued BMR MaxLoadDistance reset.";
            return true;
        }
    }

    private void UpdateLocked()
    {
        var exposedPlugin = FindBmrPlugin();
        var bmrInstalled = exposedPlugin is not null;
        var bmrLoaded = exposedPlugin?.IsLoaded == true;

        var shouldResolveInternals = configuration.ReflectionToolsEnabled
                                     || pendingRegistryRestore
                                     || pendingMaxLoadDistanceRestore
                                     || removedRegistryEntries.Count > 0;

        if (!shouldResolveInternals)
        {
            SetStatusLocked(BuildDisabledStatus(bmrInstalled
                ? "Reflection tools disabled."
                : "BossMod Reborn not found; reflection tools disabled."));
            LogAvailabilityChange(lastStatus);
            return;
        }

        if (exposedPlugin is null)
        {
            SetStatusLocked(BuildUnavailableStatus(null, false, "BossMod Reborn not found."));
            LogAvailabilityChange(lastStatus);
            return;
        }

        if (!bmrLoaded)
        {
            SetStatusLocked(BuildUnavailableStatus(exposedPlugin, false, "BossMod Reborn installed but not loaded."));
            LogAvailabilityChange(lastStatus);
            return;
        }

        if (!TryResolveContext(exposedPlugin, out var context, out var error))
        {
            SetStatusLocked(BuildUnavailableStatus(exposedPlugin, bmrLoaded, error));
            LogAvailabilityChange(lastStatus);
            return;
        }

        if (!ReferenceEquals(trackedRegistry, context.RegisteredModules))
        {
            removedRegistryEntries.Clear();
            trackedRegistry = context.RegisteredModules;
        }

        var knownHuntOids = FindKnownHuntOids(context.RegisteredModules);
        var disabledOids = new HashSet<uint>();
        if (configuration.ReflectionToolsEnabled)
        {
            if (configuration.ReflectionQueenLunatenderDisabled)
                disabledOids.Add(QueenLunatenderOid);
            if (configuration.ReflectionHuntsDisabled)
            {
                foreach (var oid in knownHuntOids)
                    disabledOids.Add(oid);
            }
        }

        var registryResult = ApplyRegistryState(context, disabledOids);
        var maxLoadDistanceResult = ApplyMaxLoadDistanceState(context);

        pendingRegistryRestore = false;
        if (pendingMaxLoadDistanceRestore && maxLoadDistanceResult.ResetApplied)
        {
            pendingMaxLoadDistanceRestore = false;
            clearMaxLoadDistanceDesiredAfterRestore = false;
        }

        var currentMaxLoadDistance = maxLoadDistanceResult.CurrentValue;
        var queueState = disabledOids.Count == 0
            ? "ready"
            : $"ready; disabling {disabledOids.Count} registry OID(s)";

        if (registryResult.AnyChanged)
        {
            lastAction = $"BMR registry updated: removed {registryResult.RemovedRegistryEntries}, restored {registryResult.RestoredRegistryEntries}, live removed {registryResult.RemovedLiveModules}.";
        }
        else if (maxLoadDistanceResult.Changed)
        {
            lastAction = maxLoadDistanceResult.Action;
        }

        SetStatusLocked(new BmrReflectionStatus
        {
            ToolsEnabled = configuration.ReflectionToolsEnabled,
            BmrInstalled = true,
            BmrLoaded = true,
            BmrName = exposedPlugin.Name,
            BmrInternalName = exposedPlugin.InternalName,
            BmrVersion = exposedPlugin.Version?.ToString(),
            ReflectionReady = true,
            ReflectionState = queueState,
            QueenDesiredDisabled = configuration.ReflectionQueenLunatenderDisabled,
            HuntsDesiredDisabled = configuration.ReflectionHuntsDisabled,
            MaxLoadDistanceDesiredMinimized = configuration.ReflectionMaxLoadDistanceMinimized,
            MinimizedMaxLoadDistance = configuration.ReflectionMinimizedMaxLoadDistance,
            CurrentMaxLoadDistance = currentMaxLoadDistance,
            HasCapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance,
            CapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance
                ? configuration.ReflectionOriginalMaxLoadDistance
                : null,
            QueenActuallyDisabled = !context.RegisteredModules.Contains(QueenLunatenderOid),
            HuntsActuallyDisabled = knownHuntOids.Count > 0 && knownHuntOids.All(oid => !context.RegisteredModules.Contains(oid)),
            DisabledRegistryEntryCount = removedRegistryEntries.Count,
            RemovedLiveModuleCount = registryResult.RemovedLiveModules,
            RestoredRegistryEntryCount = registryResult.RestoredRegistryEntries,
            RegisteredModuleCount = context.RegisteredModules.Count,
            KnownHuntModuleCount = knownHuntOids.Count,
            LastAction = lastAction,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        LogAvailabilityChange(lastStatus);
    }

    private IExposedPlugin? FindBmrPlugin()
    {
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (LooksLikeBmr(plugin))
                return plugin;
        }

        return null;
    }

    private bool TryResolveContext(IExposedPlugin exposedPlugin, out BmrReflectionContext context, out string error)
    {
        context = default!;
        error = string.Empty;

        var localPlugin = GetLocalPlugin(exposedPlugin);
        if (localPlugin is null)
        {
            error = "Could not reflect Dalamud LocalPlugin from exposed plugin.";
            return false;
        }

        var instance = GetFieldValue(localPlugin, "instance");
        if (instance is null)
        {
            error = "BossMod Reborn plugin instance not available yet.";
            return false;
        }

        var bossModuleManager = GetFieldValue(instance, "_bossmod");
        if (bossModuleManager is null)
        {
            error = "BossMod Reborn _bossmod manager not initialized yet.";
            return false;
        }

        var assembly = instance.GetType().Assembly;
        var registryType = assembly.GetType("BossMod.BossModuleRegistry");
        if (registryType is null)
        {
            error = "BossMod.BossModuleRegistry type not found.";
            return false;
        }

        var registeredModules = GetStaticFieldValue(registryType, "RegisteredModules") as IDictionary;
        if (registeredModules is null)
        {
            error = "BossModuleRegistry.RegisteredModules is not available.";
            return false;
        }

        var managerType = bossModuleManager.GetType();
        var config = GetStaticFieldValue(managerType, "Config");
        if (config is null)
        {
            error = "BossModuleManager.Config not available.";
            return false;
        }

        var pendingModules = GetFieldValue(bossModuleManager, "PendingModules") as IList;
        var loadedModules = GetFieldValue(bossModuleManager, "LoadedModules") as IList;
        if (pendingModules is null || loadedModules is null)
        {
            error = "BossModuleManager pending/loaded module lists not available.";
            return false;
        }

        var unloadModule = FindMethod(managerType, "UnloadModule", typeof(int));

        context = new BmrReflectionContext(
            exposedPlugin,
            instance,
            bossModuleManager,
            registeredModules,
            config,
            pendingModules,
            loadedModules,
            unloadModule);
        return true;
    }

    private RegistryApplyResult ApplyRegistryState(BmrReflectionContext context, HashSet<uint> disabledOids)
    {
        var removedRegistry = 0;
        var restoredRegistry = 0;
        var removedLive = 0;

        foreach (var (oid, info) in removedRegistryEntries.ToArray())
        {
            if (disabledOids.Contains(oid))
                continue;

            if (!context.RegisteredModules.Contains(oid))
            {
                context.RegisteredModules[oid] = info;
                restoredRegistry++;
            }

            removedRegistryEntries.Remove(oid);
        }

        foreach (var oid in disabledOids)
        {
            if (!context.RegisteredModules.Contains(oid))
                continue;

            var info = context.RegisteredModules[oid];
            if (info is not null)
                removedRegistryEntries[oid] = info;
            context.RegisteredModules.Remove(oid);
            removedRegistry++;
        }

        if (disabledOids.Count > 0)
            removedLive = RemoveLiveModules(context, disabledOids);

        return new RegistryApplyResult(removedRegistry, restoredRegistry, removedLive);
    }

    private MaxLoadDistanceApplyResult ApplyMaxLoadDistanceState(BmrReflectionContext context)
    {
        if (!TryGetFloatMember(context.Config, "MaxLoadDistance", out var currentValue))
            return new MaxLoadDistanceApplyResult(null, false, false, "BMR MaxLoadDistance not available.");

        if (pendingMaxLoadDistanceRestore)
        {
            var resetValue = configuration.ReflectionHasOriginalMaxLoadDistance
                ? configuration.ReflectionOriginalMaxLoadDistance
                : DefaultFallbackMaxLoadDistance;
            resetValue = Math.Clamp(resetValue, 0.1f, DefaultFallbackMaxLoadDistance);

            var changed = SetMaxLoadDistance(context.Config, resetValue);
            if (clearMaxLoadDistanceDesiredAfterRestore)
                configuration.ReflectionMaxLoadDistanceMinimized = false;
            configuration.ReflectionHasOriginalMaxLoadDistance = false;
            configuration.ReflectionOriginalMaxLoadDistance = DefaultFallbackMaxLoadDistance;
            configuration.Save();
            if (changed)
                FireConfigModified(context.Config);

            return new MaxLoadDistanceApplyResult(
                resetValue,
                changed,
                true,
                $"BMR MaxLoadDistance reset to {resetValue.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }

        if (!configuration.ReflectionToolsEnabled || !configuration.ReflectionMaxLoadDistanceMinimized)
            return new MaxLoadDistanceApplyResult(currentValue, false, false, string.Empty);

        var minimizedValue = configuration.ReflectionMinimizedMaxLoadDistance;
        if (!float.IsFinite(minimizedValue) || minimizedValue <= 0f)
        {
            minimizedValue = DefaultMinimizedMaxLoadDistance;
            configuration.ReflectionMinimizedMaxLoadDistance = minimizedValue;
            configuration.Save();
        }

        minimizedValue = Math.Clamp(minimizedValue, 0.1f, DefaultFallbackMaxLoadDistance);
        if (!configuration.ReflectionHasOriginalMaxLoadDistance)
        {
            configuration.ReflectionOriginalMaxLoadDistance = currentValue;
            configuration.ReflectionHasOriginalMaxLoadDistance = true;
            configuration.Save();
        }

        var minimizedChanged = SetMaxLoadDistance(context.Config, minimizedValue);
        if (minimizedChanged)
            FireConfigModified(context.Config);

        return new MaxLoadDistanceApplyResult(
            minimizedValue,
            minimizedChanged,
            false,
            $"BMR MaxLoadDistance minimized to {minimizedValue.ToString("0.###", CultureInfo.InvariantCulture)}.");
    }

    private int RemoveLiveModules(BmrReflectionContext context, HashSet<uint> disabledOids)
    {
        var removed = 0;

        for (var i = context.PendingModules.Count - 1; i >= 0; --i)
        {
            var module = context.PendingModules[i];
            if (module is null || !ShouldRemoveModule(module, disabledOids))
                continue;

            DisposeModule(module);
            context.PendingModules.RemoveAt(i);
            removed++;
        }

        for (var i = context.LoadedModules.Count - 1; i >= 0; --i)
        {
            var module = context.LoadedModules[i];
            if (module is null || !ShouldRemoveModule(module, disabledOids))
                continue;

            if (!TryInvokeLoadedUnload(context, i, module))
            {
                DisposeModule(module);
                context.LoadedModules.RemoveAt(i);
            }

            removed++;
        }

        return removed;
    }

    private static bool TryInvokeLoadedUnload(BmrReflectionContext context, int index, object module)
    {
        if (context.UnloadModule is null)
            return false;

        try
        {
            context.UnloadModule.Invoke(context.BossModuleManager, [index]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldRemoveModule(object module, HashSet<uint> disabledOids)
    {
        if (TryGetModulePrimaryOid(module, out var oid) && disabledOids.Contains(oid))
            return true;

        var info = GetFieldValue(module, "Info");
        return info is not null
               && TryGetUIntMember(info, "PrimaryActorOID", out oid)
               && disabledOids.Contains(oid);
    }

    private static bool TryGetModulePrimaryOid(object module, out uint oid)
    {
        oid = 0;
        var primaryActor = GetFieldValue(module, "PrimaryActor");
        return primaryActor is not null && TryGetUIntMember(primaryActor, "OID", out oid);
    }

    private HashSet<uint> FindKnownHuntOids(IDictionary registeredModules)
    {
        var huntOids = new HashSet<uint>();
        foreach (DictionaryEntry entry in registeredModules)
        {
            if (TryConvertUInt(entry.Key, out var oid) && entry.Value is not null && IsHuntInfo(entry.Value))
                huntOids.Add(oid);
        }

        foreach (var (oid, info) in removedRegistryEntries)
        {
            if (IsHuntInfo(info))
                huntOids.Add(oid);
        }

        return huntOids;
    }

    private static bool IsHuntInfo(object info)
    {
        var category = GetMemberValue(info, "Category")?.ToString();
        var groupType = GetMemberValue(info, "GroupType")?.ToString();
        return string.Equals(category, "Hunt", StringComparison.Ordinal)
               || string.Equals(groupType, "Hunt", StringComparison.Ordinal);
    }

    private bool SetMaxLoadDistance(object config, float value)
    {
        if (TryGetFloatMember(config, "MaxLoadDistance", out var current) && Math.Abs(current - value) < 0.001f)
            return false;

        if (TrySetMember(config, "MaxLoadDistance", value))
            return true;

        lastAction = "BMR MaxLoadDistance member not writable.";
        return false;
    }

    private void FireConfigModified(object config)
    {
        try
        {
            var modified = GetMemberValue(config, "Modified");
            var fire = modified?.GetType().GetMethod("Fire", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
            fire?.Invoke(modified, []);
        }
        catch (Exception ex)
        {
            lastAction = $"BMR config Modified.Fire failed: {ex.Message}";
            log.Warning(ex, "[ADS] BMR reflection could not fire config Modified event.");
        }
    }

    private BmrReflectionStatus BuildDisabledStatus(string state)
    {
        var exposedPlugin = FindBmrPlugin();
        return new BmrReflectionStatus
        {
            ToolsEnabled = configuration.ReflectionToolsEnabled,
            BmrInstalled = exposedPlugin is not null,
            BmrLoaded = exposedPlugin?.IsLoaded == true,
            BmrName = exposedPlugin?.Name ?? string.Empty,
            BmrInternalName = exposedPlugin?.InternalName ?? string.Empty,
            BmrVersion = exposedPlugin?.Version?.ToString(),
            ReflectionReady = false,
            ReflectionState = state,
            QueenDesiredDisabled = configuration.ReflectionQueenLunatenderDisabled,
            HuntsDesiredDisabled = configuration.ReflectionHuntsDisabled,
            MaxLoadDistanceDesiredMinimized = configuration.ReflectionMaxLoadDistanceMinimized,
            MinimizedMaxLoadDistance = configuration.ReflectionMinimizedMaxLoadDistance,
            HasCapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance,
            CapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance
                ? configuration.ReflectionOriginalMaxLoadDistance
                : null,
            LastAction = lastAction,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }

    private BmrReflectionStatus BuildUnavailableStatus(IExposedPlugin? exposedPlugin, bool bmrLoaded, string error)
        => new()
        {
            ToolsEnabled = configuration.ReflectionToolsEnabled,
            BmrInstalled = exposedPlugin is not null,
            BmrLoaded = bmrLoaded,
            BmrName = exposedPlugin?.Name ?? string.Empty,
            BmrInternalName = exposedPlugin?.InternalName ?? string.Empty,
            BmrVersion = exposedPlugin?.Version?.ToString(),
            ReflectionReady = false,
            ReflectionState = "unavailable",
            Error = error,
            QueenDesiredDisabled = configuration.ReflectionQueenLunatenderDisabled,
            HuntsDesiredDisabled = configuration.ReflectionHuntsDisabled,
            MaxLoadDistanceDesiredMinimized = configuration.ReflectionMaxLoadDistanceMinimized,
            MinimizedMaxLoadDistance = configuration.ReflectionMinimizedMaxLoadDistance,
            HasCapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance,
            CapturedMaxLoadDistance = configuration.ReflectionHasOriginalMaxLoadDistance
                ? configuration.ReflectionOriginalMaxLoadDistance
                : null,
            LastAction = lastAction,
            UpdatedAtUtc = DateTime.UtcNow,
        };

    private void SetStatusLocked(BmrReflectionStatus status)
        => lastStatus = status;

    private void LogAvailabilityChange(BmrReflectionStatus status)
    {
        var key = $"{status.ToolsEnabled}|{status.BmrInstalled}|{status.BmrLoaded}|{status.ReflectionReady}|{status.ReflectionState}|{status.Error}";
        if (string.Equals(key, lastAvailabilityKey, StringComparison.Ordinal))
            return;

        lastAvailabilityKey = key;
        log.Information($"[ADS] BMR reflection status changed: enabled={status.ToolsEnabled}, installed={status.BmrInstalled}, loaded={status.BmrLoaded}, ready={status.ReflectionReady}, state={status.ReflectionState}, error={status.Error ?? "(none)"}");
    }

    private static bool LooksLikeBmr(IExposedPlugin plugin)
    {
        var name = plugin.Name ?? string.Empty;
        var internalName = plugin.InternalName ?? string.Empty;

        return string.Equals(name, "BossMod Reborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "BossModReborn", StringComparison.OrdinalIgnoreCase)
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

    private static void DisposeModule(object module)
    {
        if (module is IDisposable disposable)
            disposable.Dispose();
    }

    private static object? GetFieldValue(object instance, string name)
        => FindField(instance.GetType(), name)?.GetValue(instance);

    private static object? GetStaticFieldValue(Type type, string name)
        => FindField(type, name)?.GetValue(null);

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

    private static bool TryGetFloatMember(object instance, string name, out float value)
    {
        value = 0f;
        var raw = GetMemberValue(instance, name);
        if (raw is null)
            return false;

        try
        {
            value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetUIntMember(object instance, string name, out uint value)
    {
        value = 0;
        var raw = GetMemberValue(instance, name);
        return TryConvertUInt(raw, out value);
    }

    private static bool TryConvertUInt(object? raw, out uint value)
    {
        value = 0;
        if (raw is null)
            return false;

        try
        {
            value = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
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

    private static MethodInfo? FindMethod(Type type, string name, params Type[] parameterTypes)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: parameterTypes,
                modifiers: null);
            if (method is not null)
                return method;
        }

        return null;
    }

    private sealed record BmrReflectionContext(
        IExposedPlugin ExposedPlugin,
        object Instance,
        object BossModuleManager,
        IDictionary RegisteredModules,
        object Config,
        IList PendingModules,
        IList LoadedModules,
        MethodInfo? UnloadModule);

    private readonly record struct RegistryApplyResult(
        int RemovedRegistryEntries,
        int RestoredRegistryEntries,
        int RemovedLiveModules)
    {
        public bool AnyChanged => RemovedRegistryEntries > 0 || RestoredRegistryEntries > 0 || RemovedLiveModules > 0;
    }

    private readonly record struct MaxLoadDistanceApplyResult(
        float? CurrentValue,
        bool Changed,
        bool ResetApplied,
        string Action);
}
