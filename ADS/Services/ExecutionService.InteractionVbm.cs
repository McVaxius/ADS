using System.Reflection;
using ADS.Models;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private InteractionVbmPause? interactionVbmPause;

    internal Func<InteractionVbmReadResult>? ReadInteractionVbmOverride { get; set; }
    internal Func<InteractionVbmSnapshot, bool, bool>? WriteInteractionVbmAiOverride { get; set; }
    internal Func<InteractionVbmSnapshot, bool, IReadOnlyList<string>, bool>? WriteInteractionVbmPresetsOverride { get; set; }
    internal Func<ulong>? InteractionVbmCharacterIdOverride { get; set; }

    public bool IsInteractionVbmPauseActive => interactionVbmPause?.Confirmed == true;
    public string InteractionVbmPauseStatus { get; private set; } = "No VBM interaction pause.";

    internal bool TryPauseInteractionVbm(DutyContextSnapshot context)
    {
        var read = ReadInteractionVbm();
        if (!read.Loaded)
        {
            interactionVbmPause = null;
            InteractionVbmPauseStatus = "VBM is not loaded; no interaction pause needed.";
            return true;
        }
        if (read.Snapshot is not { } current || !TryReadInteractionVbmOwner(context, out var owner))
        {
            InteractionVbmPauseStatus = read.Snapshot is null ? read.Reason : "ADS interaction owner is unavailable.";
            return false;
        }
        if (interactionVbmPause is { } retained)
        {
            if (retained.Owner != owner || !InteractionVbmMatchesExpected(current, retained.Expected))
            {
                interactionVbmPause = null;
                InteractionVbmPauseStatus = "VBM or ADS ownership changed; interaction held without restoring previous state.";
                return false;
            }
            retained.Expected = current;
            return retained.Confirmed && !current.AiEnabled && current.ForceDisabled;
        }

        // A disabled AI and no runnable presets must never be enabled by cleanup.
        if (!current.AiEnabled && current.ForceDisabled)
        {
            interactionVbmPause = new InteractionVbmPause(owner, current) { Confirmed = true };
            InteractionVbmPauseStatus = "VBM was already off; its state is retained.";
            return true;
        }

        var pause = new InteractionVbmPause(owner, current);
        interactionVbmPause = pause;
        if (current.AiEnabled && !WriteInteractionVbmAi(pause, context, false)
            || !pause.Expected.ForceDisabled && !WriteInteractionVbmPresets(pause, context, true, Array.Empty<string>(), exactNames: false)
            || !TryReadOwnedInteractionVbm(pause, context, out var paused)
            || paused.AiEnabled || !paused.ForceDisabled)
        {
            ReleaseInteractionVbmPause(context, allowRestore: true);
            InteractionVbmPauseStatus = "VBM pause could not be confirmed; interaction held.";
            return false;
        }
        pause.Confirmed = true;
        InteractionVbmPauseStatus = "VBM AI and autorotation paused for the ADS interaction.";
        return true;
    }

    internal void ReleaseInteractionVbmPause(DutyContextSnapshot? context, bool allowRestore)
    {
        if (interactionVbmPause is not { } pause)
            return;
        pause.Confirmed = false;
        try
        {
            if (!allowRestore || context is null)
            {
                InteractionVbmPauseStatus = "VBM interaction pause discarded without enabling AI or presets.";
                return;
            }
            if (!TryReadOwnedInteractionVbm(pause, context, out _))
            {
                InteractionVbmPauseStatus = "VBM or ADS ownership changed; current state retained.";
                return;
            }
            // Changing AI rebuilds the hidden Multibox preset. Restore AI first,
            // then the saved literal preset order and force-disabled state.
            if (pause.Expected.AiEnabled != pause.Original.AiEnabled
                && !WriteInteractionVbmAi(pause, context, pause.Original.AiEnabled))
            {
                InteractionVbmPauseStatus = "VBM AI restoration could not be confirmed.";
                return;
            }
            if ((pause.Expected.ForceDisabled != pause.Original.ForceDisabled
                    || !pause.Expected.ActivePresets.SequenceEqual(pause.Original.ActivePresets, StringComparer.Ordinal))
                && !WriteInteractionVbmPresets(pause, context, pause.Original.ForceDisabled, pause.Original.ActivePresets, exactNames: true))
            {
                InteractionVbmPauseStatus = "VBM preset restoration could not be confirmed.";
                return;
            }
            InteractionVbmPauseStatus = TryReadOwnedInteractionVbm(pause, context, out var restored)
                && InteractionVbmMatches(restored, pause.Original)
                ? "VBM interaction state restored exactly."
                : "VBM interaction restoration could not be confirmed.";
        }
        finally
        {
            if (ReferenceEquals(interactionVbmPause, pause))
                interactionVbmPause = null;
        }
    }

    private bool TryReadInteractionVbmOwner(DutyContextSnapshot context, out InteractionVbmOwner owner)
    {
        owner = default;
        if (!IsOwned || !context.PluginEnabled || !context.IsLoggedIn || context.IsUnsafeTransition)
            return false;
        try
        {
            var characterId = InteractionVbmCharacterIdOverride?.Invoke() ?? Plugin.PlayerState?.ContentId ?? 0;
            if (characterId == 0)
                return false;
            owner = new(characterId, context.TerritoryTypeId, context.ContentFinderConditionId, context.InInstancedDuty, CurrentMode);
            return true;
        }
        catch { return false; }
    }

    private bool TryReadOwnedInteractionVbm(InteractionVbmPause pause, DutyContextSnapshot context, out InteractionVbmSnapshot current)
    {
        current = null!;
        if (!ReferenceEquals(interactionVbmPause, pause) || !TryReadInteractionVbmOwner(context, out var owner) || owner != pause.Owner)
            return false;
        var read = ReadInteractionVbm();
        if (read.Snapshot is not { } snapshot || !InteractionVbmMatchesExpected(snapshot, pause.Expected))
            return false;
        pause.Expected = snapshot;
        current = snapshot;
        return true;
    }

    private bool WriteInteractionVbmAi(InteractionVbmPause pause, DutyContextSnapshot context, bool enabled)
    {
        if (!TryReadOwnedInteractionVbm(pause, context, out var before))
            return false;
        var submitted = false;
        try
        {
            submitted = WriteInteractionVbmAiOverride is { } write ? write(before, enabled) : WriteNativeInteractionVbmAi(enabled);
        }
        catch { }
        var after = ReadInteractionVbm().Snapshot;
        if (after is null || !InteractionVbmSameIdentity(before, after)
            || !ReferenceEquals(interactionVbmPause, pause)
            || !TryReadInteractionVbmOwner(context, out var owner) || owner != pause.Owner)
            return false;
        var namesWithoutMultibox = before.ActivePresets.Where(name => name != "VBM Multibox");
        var expectedNames = enabled ? namesWithoutMultibox.Append("VBM Multibox") : namesWithoutMultibox;
        if (after.AiEnabled != enabled
            || after.ForceDisabled != before.ForceDisabled
            || !after.ActivePresets.SequenceEqual(before.ActivePresets, StringComparer.Ordinal)
                && !after.ActivePresets.SequenceEqual(expectedNames, StringComparer.Ordinal))
            return false;
        // Retain confirmed synchronous setter effects even on rejection so a
        // partially applied pause can use the same guarded cleanup path.
        pause.Expected = after;
        return submitted && after.AiEnabled == enabled;
    }

    private bool WriteInteractionVbmPresets(InteractionVbmPause pause, DutyContextSnapshot context, bool forceDisabled,
        IReadOnlyList<string> names, bool exactNames)
    {
        if (!TryReadOwnedInteractionVbm(pause, context, out var before))
            return false;
        var submitted = false;
        try
        {
            submitted = WriteInteractionVbmPresetsOverride is { } write ? write(before, forceDisabled, names.ToArray())
                : WriteNativeInteractionVbmPresets(forceDisabled, names);
        }
        catch { }
        var after = ReadInteractionVbm().Snapshot;
        if (after is null || !InteractionVbmSameIdentity(before, after)
            || !ReferenceEquals(interactionVbmPause, pause)
            || !TryReadInteractionVbmOwner(context, out var owner) || owner != pause.Owner)
            return false;
        if (after.AiEnabled != before.AiEnabled || after.ForceDisabled != forceDisabled
            || (exactNames ? !after.ActivePresets.SequenceEqual(names, StringComparer.Ordinal)
                : after.ActivePresets.Count != 0 && !after.ActivePresets.SequenceEqual(before.ActivePresets, StringComparer.Ordinal)))
            return false;
        pause.Expected = after;
        return submitted && after.AiEnabled == before.AiEnabled && after.ForceDisabled == forceDisabled
            && (!exactNames || after.ActivePresets.SequenceEqual(names, StringComparer.Ordinal));
    }

    private InteractionVbmReadResult ReadInteractionVbm()
    {
        try
        {
            var read = ReadInteractionVbmOverride?.Invoke() ?? ReadNativeInteractionVbm();
            if (read.Snapshot is not { } state)
                return read;
            if (!read.Loaded || state.Provider is null || state.AiConfig is null || state.PresetRuntime is null
                || state.RotationDatabase is null || state.ActivePresets is null || state.ActivePresets.Any(name => name is null))
                return new(true, null, "VBM state is unreadable; interaction held.");
            // Native force-disable is a private empty-name sentinel, not a
            // selectable preset. Preserve its flag without replaying that name.
            var names = state.ForceDisabled && state.ActivePresets.SequenceEqual(new[] { string.Empty }, StringComparer.Ordinal)
                ? Array.Empty<string>() : state.ActivePresets.ToArray();
            return read with { Snapshot = state with { ActivePresets = names } };
        }
        catch { return new(true, null, "VBM state is unavailable; interaction held."); }
    }

    private static bool InteractionVbmSameIdentity(InteractionVbmSnapshot first, InteractionVbmSnapshot second)
        => ReferenceEquals(first.Provider, second.Provider) && ReferenceEquals(first.AiConfig, second.AiConfig)
            && ReferenceEquals(first.PresetRuntime, second.PresetRuntime) && ReferenceEquals(first.RotationDatabase, second.RotationDatabase);

    private static bool InteractionVbmMatches(InteractionVbmSnapshot first, InteractionVbmSnapshot second)
        => InteractionVbmSameIdentity(first, second) && first.AiEnabled == second.AiEnabled && first.ForceDisabled == second.ForceDisabled
            && first.ActivePresets.SequenceEqual(second.ActivePresets, StringComparer.Ordinal);

    private static bool InteractionVbmMatchesExpected(InteractionVbmSnapshot current, InteractionVbmSnapshot expected)
    {
        if (!InteractionVbmSameIdentity(current, expected) || current.AiEnabled != expected.AiEnabled
            || current.ForceDisabled != expected.ForceDisabled)
            return false;
        if (current.ActivePresets.SequenceEqual(expected.ActivePresets, StringComparer.Ordinal))
            return true;
        // VBM's native update removes its hidden Multibox entry and appends it
        // only while AI is enabled and presets are not force-disabled.
        var names = expected.ActivePresets.Where(name => name != "VBM Multibox");
        return current.ActivePresets.SequenceEqual(current.AiEnabled && !current.ForceDisabled
            ? names.Append("VBM Multibox") : names, StringComparer.Ordinal);
    }

    private static InteractionVbmReadResult ReadNativeInteractionVbm()
    {
        var pluginInterface = Plugin.PluginInterface;
        if (pluginInterface is null)
            return new(false, null, "No plugin host is available.");
        var loaded = pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded
            && plugin.InternalName is "BossMod" or "BossModReborn").ToArray();
        if (!loaded.Any(plugin => plugin.InternalName == "BossMod"))
            return new(false, null, "VBM is not loaded.");
        if (loaded.Length != 1 || loaded[0].InternalName != "BossMod")
            return new(true, null, "BossMod IPC provider is ambiguous; interaction held.");

        var localPlugin = FindInteractionVbmLocalPlugin(loaded[0], loaded[0].GetType().Assembly, 4, new(ReferenceEqualityComparer.Instance));
        var provider = localPlugin is null ? null : ReadInteractionVbmMember(localPlugin, "instance");
        var assembly = localPlugin is null ? null : ReadInteractionVbmMember(localPlugin, "Assembly") as Assembly;
        if (provider is null || assembly is null || !ReferenceEquals(provider.GetType().Assembly, assembly))
            return new(true, null, "VBM live instance is unavailable; interaction held.");

        var host = ReadInteractionVbmMember(provider, "Host");
        var tickType = assembly.GetType("BossMod.Services.TickService");
        var services = host is null ? null : ReadInteractionVbmMember(host, "Services") as IServiceProvider;
        var tick = tickType is null ? null : services?.GetService(tickType);
        var database = tick is null ? null : ReadInteractionVbmMember(tick, "_rotationDB");
        var serviceType = assembly.GetType("BossMod.Service");
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var config = serviceType?.GetProperty("Config", statics)?.GetValue(null) ?? serviceType?.GetField("Config", statics)?.GetValue(null);
        var aiType = assembly.GetType("BossMod.AI.AIConfig");
        var ai = config is null || aiType is null ? null : ReadInteractionVbmAiConfig(config, aiType, assembly);
        if (tick is null || tick.GetType() != tickType || database is null || ai is null
            || ReadInteractionVbmMember(ai, "Enabled") is not bool aiEnabled)
            return new(true, null, "VBM runtime or AI configuration is unreadable; interaction held.");

        var getDisabled = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled");
        var getActive = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");
        if (!getDisabled.HasFunction || !getActive.HasFunction
            || !pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled").HasFunction
            || !pluginInterface.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList").HasFunction
            || !pluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration").HasFunction)
            return new(true, null, "VBM pause or restoration API is unavailable; interaction held.");
        var disabled = getDisabled.InvokeFunc();
        var active = getActive.InvokeFunc();
        if (active is null || active.Any(name => name is null))
            return new(true, null, "VBM active presets are unreadable; interaction held.");
        return new(true, new(provider, ai, tick, database, aiEnabled, disabled, active.ToArray()), string.Empty);
    }

    private static bool WriteNativeInteractionVbmAi(bool enabled)
        => Plugin.PluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
            .InvokeFunc(new List<string> { "AIConfig", "Enabled", enabled.ToString() }, true) is not null;

    internal static object? ReadInteractionVbmAiConfig(object root, Type aiType, Assembly liveAssembly)
    {
        if (!ReferenceEquals(root.GetType().Assembly, liveAssembly) || !ReferenceEquals(aiType.Assembly, liveAssembly))
            return null;
        var getters = root.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == "Get" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0)
            .Take(2).ToArray();
        if (getters.Length != 1)
            return null;
        try
        {
            var node = getters[0].MakeGenericMethod(aiType).Invoke(root, null);
            return node is not null && node.GetType() == aiType && ReferenceEquals(node.GetType().Assembly, liveAssembly)
                ? node : null;
        }
        catch { return null; }
    }

    private static bool WriteNativeInteractionVbmPresets(bool forceDisabled, IReadOnlyList<string> names)
    {
        var pluginInterface = Plugin.PluginInterface;
        if (!forceDisabled || names.Count != 0)
        {
            if (!pluginInterface.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList").InvokeFunc(names.ToList()))
                return false;
        }
        return !forceDisabled || pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled").InvokeFunc();
    }

    private static object? ReadInteractionVbmMember(object root, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = root.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, flags);
            if (property is not null)
                return property.GetValue(root);
            var field = type.GetField(name, flags);
            if (field is not null)
                return field.GetValue(root);
        }
        return null;
    }

    private static object? FindInteractionVbmLocalPlugin(object root, Assembly dalamudAssembly, int depth, HashSet<object> visited)
    {
        for (var type = root.GetType(); type is not null; type = type.BaseType)
            if (type.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin")
                return root;
        if (depth <= 0 || !visited.Add(root))
            return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = root.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers(flags))
            {
                object? value;
                try
                {
                    value = member switch
                    {
                        FieldInfo field => field.GetValue(root),
                        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(root),
                        _ => null,
                    };
                }
                catch { continue; }
                if (value is null || value.GetType().Assembly != dalamudAssembly)
                    continue;
                var local = FindInteractionVbmLocalPlugin(value, dalamudAssembly, depth - 1, visited);
                if (local is not null)
                    return local;
            }
        }
        return null;
    }

    internal sealed record InteractionVbmReadResult(bool Loaded, InteractionVbmSnapshot? Snapshot, string Reason);
    internal sealed record InteractionVbmSnapshot(object Provider, object AiConfig, object PresetRuntime, object RotationDatabase,
        bool AiEnabled, bool ForceDisabled, IReadOnlyList<string> ActivePresets);
    private readonly record struct InteractionVbmOwner(ulong CharacterId, uint Territory, uint Content, bool InDuty, OwnershipMode Mode);
    private sealed class InteractionVbmPause(InteractionVbmOwner owner, InteractionVbmSnapshot original)
    {
        internal InteractionVbmOwner Owner { get; } = owner;
        internal InteractionVbmSnapshot Original { get; } = original;
        internal InteractionVbmSnapshot Expected { get; set; } = original;
        internal bool Confirmed { get; set; }
    }
}
