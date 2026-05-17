using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ADS.Services;

public sealed unsafe class HigherLowerServerEventTraceService : IDisposable
{
    private const int MaxRows = 700;
    private const int MaxPendingRows = 2048;
    private const int MaxLegacyMapEffectBytes = 512;
    private const float HigherLowerNearbyRadius = 80f;

    private const string ActorControlSignature = "E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64";
    private const string MapEffectSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8";
    private const string MapEffectNSelectorSignature = "40 55 41 57 48 83 EC ?? 48 83 B9";
    private const string LegacyMapEffectSignature = "89 54 24 10 48 89 4C 24 ?? 53 56 57 41 55 41 57 48 83 EC 30 48 8B 99 ?? ?? ?? ??";
    private const string OpenTreasureSignature = "40 53 48 83 EC 20 48 8B DA 48 8D 0D ?? ?? ?? ?? 8B 52 10 E8 ?? ?? ?? ?? 48 85 C0 74 1B";
    private const string SystemLogSignature = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 0F B6 47 28";

    private static readonly HashSet<uint> HigherLowerSystemLogAnchors = [6966, 6967, 6972, 2031];

    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly TreasureHighLowDiagnosticService diagnostics;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly Queue<RawServerEvent> pending = new();
    private readonly List<ServerEventRow> rows = new();
    private readonly List<string> hookStatus = new();

    private Hook<ProcessPacketActorControlDelegate>? actorControlHook;
    private Hook<ProcessMapEffectDelegate>? mapEffectHook;
    private Hook<ProcessMapEffectNDelegate>? mapEffect1Hook;
    private Hook<ProcessMapEffectNDelegate>? mapEffect2Hook;
    private Hook<ProcessMapEffectNDelegate>? mapEffect3Hook;
    private Hook<ProcessLegacyMapEffectDelegate>? legacyMapEffectHook;
    private Hook<ProcessPacketOpenTreasureDelegate>? openTreasureHook;
    private Hook<ProcessSystemLogMessageDelegate>? systemLogHook;
    private ulong rowSequence;

    private delegate void ProcessPacketActorControlDelegate(
        uint actorId,
        uint category,
        uint p1,
        uint p2,
        uint p3,
        uint p4,
        uint p5,
        uint p6,
        uint p7,
        uint p8,
        ulong targetId,
        byte replaying);

    private delegate void ProcessMapEffectDelegate(void* self, uint index, ushort s1, ushort s2);
    private delegate void ProcessMapEffectNDelegate(ContentDirector* director, byte* packet);
    private delegate byte ProcessLegacyMapEffectDelegate(EventFramework* fwk, EventId eventId, byte seq, byte unk, void* data, ulong length);
    private delegate void ProcessPacketOpenTreasureDelegate(uint playerId, byte* packet);
    private delegate void* ProcessSystemLogMessageDelegate(uint entityId, uint logMessageId, int* args, byte argCount);

    public HigherLowerServerEventTraceService(
        IObjectTable objectTable,
        IClientState clientState,
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        TreasureHighLowDiagnosticService diagnostics,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.sigScanner = sigScanner;
        this.gameInteropProvider = gameInteropProvider;
        this.diagnostics = diagnostics;
        this.log = log;

        InstallHooks();
    }

    public int InstalledHookCount { get; private set; }

    public int PendingCount
    {
        get
        {
            lock (gate)
                return pending.Count;
        }
    }

    public IReadOnlyList<string> HookStatus
    {
        get
        {
            lock (gate)
                return hookStatus.ToList();
        }
    }

    public IReadOnlyList<ServerEventRow> GetRowsSnapshot()
    {
        lock (gate)
            return rows.ToList();
    }

    public void Clear()
    {
        lock (gate)
        {
            rows.Clear();
            pending.Clear();
        }
    }

    public void Update(DutyContextSnapshot context)
    {
        List<RawServerEvent> drained;
        lock (gate)
        {
            if (pending.Count == 0)
                return;

            drained = pending.ToList();
            pending.Clear();
        }

        foreach (var raw in drained)
        {
            var row = BuildRow(raw, context);
            lock (gate)
            {
                rows.Add(row);
                while (rows.Count > MaxRows)
                    rows.RemoveAt(0);
            }

            diagnostics.RecordServerEvent(row);
        }
    }

    public void Dispose()
    {
        DisposeHook(ref systemLogHook);
        DisposeHook(ref openTreasureHook);
        DisposeHook(ref legacyMapEffectHook);
        DisposeHook(ref mapEffect3Hook);
        DisposeHook(ref mapEffect2Hook);
        DisposeHook(ref mapEffect1Hook);
        DisposeHook(ref mapEffectHook);
        DisposeHook(ref actorControlHook);
    }

    private void InstallHooks()
    {
        InstallHook<ProcessPacketActorControlDelegate>(
            "ActorControl",
            () => gameInteropProvider.HookFromSignature<ProcessPacketActorControlDelegate>(ActorControlSignature, ProcessPacketActorControlDetour),
            hook => actorControlHook = hook);
        InstallHook<ProcessMapEffectDelegate>(
            "MapEffect",
            () => gameInteropProvider.HookFromSignature<ProcessMapEffectDelegate>(MapEffectSignature, ProcessMapEffectDetour),
            hook => mapEffectHook = hook);
        InstallMapEffectPacketHooks();
        InstallHook<ProcessLegacyMapEffectDelegate>(
            "LegacyMapEffect",
            () => gameInteropProvider.HookFromSignature<ProcessLegacyMapEffectDelegate>(LegacyMapEffectSignature, ProcessLegacyMapEffectDetour),
            hook => legacyMapEffectHook = hook);
        InstallHook<ProcessPacketOpenTreasureDelegate>(
            "OpenTreasure",
            () => gameInteropProvider.HookFromSignature<ProcessPacketOpenTreasureDelegate>(OpenTreasureSignature, ProcessPacketOpenTreasureDetour),
            hook => openTreasureHook = hook);
        InstallHook<ProcessSystemLogMessageDelegate>(
            "SystemLog",
            () => gameInteropProvider.HookFromSignature<ProcessSystemLogMessageDelegate>(SystemLogSignature, ProcessSystemLogMessageDetour),
            hook => systemLogHook = hook);
    }

    private void InstallMapEffectPacketHooks()
    {
        try
        {
            var addresses = sigScanner.ScanAllText(MapEffectNSelectorSignature);
            if (addresses.Length != 3)
            {
                AddHookStatus($"MapEffectN unavailable: expected 3 signatures, found {addresses.Length}.");
                return;
            }

            mapEffect1Hook = gameInteropProvider.HookFromAddress<ProcessMapEffectNDelegate>(addresses[0], ProcessMapEffect1Detour);
            mapEffect1Hook.Enable();
            mapEffect2Hook = gameInteropProvider.HookFromAddress<ProcessMapEffectNDelegate>(addresses[1], ProcessMapEffect2Detour);
            mapEffect2Hook.Enable();
            mapEffect3Hook = gameInteropProvider.HookFromAddress<ProcessMapEffectNDelegate>(addresses[2], ProcessMapEffect3Detour);
            mapEffect3Hook.Enable();
            InstalledHookCount += 3;
            AddHookStatus($"MapEffectN hooks installed at 0x{addresses[0]:X}, 0x{addresses[1]:X}, 0x{addresses[2]:X}.");
        }
        catch (Exception ex)
        {
            AddHookStatus($"MapEffectN unavailable: {ex.GetType().Name}: {ex.Message}");
            log.Warning(ex, "[ADS][ServerEvents] Failed to install MapEffectN hooks.");
        }
    }

    private void InstallHook<T>(string name, Func<Hook<T>> createHook, Action<Hook<T>> assignHook)
        where T : Delegate
    {
        try
        {
            var hook = createHook();
            assignHook(hook);
            hook.Enable();
            InstalledHookCount++;
            AddHookStatus($"{name} hook installed.");
        }
        catch (Exception ex)
        {
            AddHookStatus($"{name} unavailable: {ex.GetType().Name}: {ex.Message}");
            log.Warning(ex, $"[ADS][ServerEvents] Failed to install {name} hook.");
        }
    }

    private void AddHookStatus(string value)
    {
        lock (gate)
            hookStatus.Add(value);
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        hook?.Dispose();
        hook = null;
    }

    private void ProcessPacketActorControlDetour(
        uint actorId,
        uint category,
        uint p1,
        uint p2,
        uint p3,
        uint p4,
        uint p5,
        uint p6,
        uint p7,
        uint p8,
        ulong targetId,
        byte replaying)
    {
        actorControlHook?.Original(actorId, category, p1, p2, p3, p4, p5, p6, p7, p8, targetId, replaying);

        try
        {
            var kind = (ActorControlCategory)category switch
            {
                ActorControlCategory.EObjAnimation => ServerEventKind.EObjAnim,
                ActorControlCategory.EObjSetState => ServerEventKind.EObjState,
                ActorControlCategory.PlayActionTimeline => ServerEventKind.Timeline,
                _ => ServerEventKind.Unknown,
            };

            if (kind == ServerEventKind.Unknown)
                return;

            Enqueue(RawServerEvent.ActorControl(kind, actorId, category, p1, p2, p3, p4, p5, p6, p7, p8, targetId, replaying));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ServerEvents] ActorControl detour failed.");
        }
    }

    private void ProcessMapEffectDetour(void* self, uint index, ushort s1, ushort s2)
    {
        mapEffectHook?.Original(self, index, s1, s2);
        try
        {
            Enqueue(RawServerEvent.MapEffect("MapEffect", index, s1, s2));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ServerEvents] MapEffect detour failed.");
        }
    }

    private void ProcessMapEffect1Detour(ContentDirector* director, byte* packet)
    {
        mapEffect1Hook?.Original(director, packet);
        ProcessMapEffectPacket("MapEffect1", packet, 10, 18);
    }

    private void ProcessMapEffect2Detour(ContentDirector* director, byte* packet)
    {
        mapEffect2Hook?.Original(director, packet);
        ProcessMapEffectPacket("MapEffect2", packet, 18, 34);
    }

    private void ProcessMapEffect3Detour(ContentDirector* director, byte* packet)
    {
        mapEffect3Hook?.Original(director, packet);
        ProcessMapEffectPacket("MapEffect3", packet, 26, 50);
    }

    private void ProcessMapEffectPacket(string source, byte* data, byte offLow, byte offIndex)
    {
        try
        {
            if (data == null)
                return;

            var count = *data;
            for (var i = 0; i < count; i++)
            {
                var low = *(ushort*)(data + 2 * i + offLow);
                var high = *(ushort*)(data + 2 * i + 2);
                var index = data[i + offIndex];
                Enqueue(RawServerEvent.MapEffect(source, index, low, high));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS][ServerEvents] {source} detour failed.");
        }
    }

    private byte ProcessLegacyMapEffectDetour(EventFramework* fwk, EventId eventId, byte seq, byte unk, void* data, ulong length)
    {
        var result = legacyMapEffectHook?.Original(fwk, eventId, seq, unk, data, length) ?? (byte)0;

        try
        {
            var capturedLength = (int)Math.Min(length, MaxLegacyMapEffectBytes);
            var bytes = data == null || capturedLength <= 0
                ? []
                : new Span<byte>(data, capturedLength).ToArray();
            Enqueue(RawServerEvent.LegacyMapEffect(eventId.Id, seq, unk, length, bytes, length > MaxLegacyMapEffectBytes));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ServerEvents] LegacyMapEffect detour failed.");
        }

        return result;
    }

    private void ProcessPacketOpenTreasureDetour(uint playerId, byte* packet)
    {
        openTreasureHook?.Original(playerId, packet);

        try
        {
            var actorId = packet == null ? 0u : *(uint*)(packet + 16);
            Enqueue(RawServerEvent.OpenTreasure(playerId, actorId));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ServerEvents] OpenTreasure detour failed.");
        }
    }

    private void* ProcessSystemLogMessageDetour(uint entityId, uint logMessageId, int* args, byte argCount)
    {
        void* result = null;
        if (systemLogHook != null)
            result = systemLogHook.Original(entityId, logMessageId, args, argCount);

        try
        {
            if (!HigherLowerSystemLogAnchors.Contains(logMessageId))
                return result;

            var count = Math.Min(argCount, (byte)16);
            var capturedArgs = args == null || count == 0
                ? []
                : new Span<int>(args, count).ToArray();
            Enqueue(RawServerEvent.SystemLog(entityId, logMessageId, capturedArgs, argCount));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ServerEvents] SystemLog detour failed.");
        }

        return result;
    }

    private void Enqueue(RawServerEvent raw)
    {
        lock (gate)
        {
            pending.Enqueue(raw);
            while (pending.Count > MaxPendingRows)
                pending.Dequeue();
        }
    }

    private ServerEventRow BuildRow(RawServerEvent raw, DutyContextSnapshot context)
    {
        var objectInfo = raw.ActorId != 0 ? ResolveObject(raw.ActorId) : null;
        var stateData = FormatStateData(raw);
        var sourceParams = FormatSourceParams(raw);
        var relevant = IsHigherLowerRelevant(raw, objectInfo, context);
        var bossModKind = ToBossModKind(raw.Kind);

        return new ServerEventRow(
            Sequence: ++rowSequence,
            TimestampUtc: raw.TimestampUtc,
            Kind: raw.Kind,
            BossModKind: bossModKind,
            TerritoryId: context.TerritoryTypeId != 0 ? context.TerritoryTypeId : clientState.TerritoryType,
            MapId: context.MapId,
            ActorId: raw.ActorId,
            TargetId: raw.TargetId,
            ObjectName: objectInfo?.Name ?? string.Empty,
            ObjectKind: objectInfo?.ObjectKind ?? string.Empty,
            GameObjectId: objectInfo?.GameObjectId ?? 0,
            EntityId: objectInfo?.EntityId ?? raw.ActorId,
            BaseId: objectInfo?.BaseId ?? 0,
            LayoutId: objectInfo?.LayoutId ?? 0,
            GimmickId: objectInfo?.GimmickId ?? 0,
            EventState: objectInfo?.EventState ?? 0,
            EventId: objectInfo?.EventId ?? 0,
            Targetable: objectInfo?.Targetable,
            Position: objectInfo?.Position,
            Distance: objectInfo?.Distance,
            StateData: stateData,
            SourceParams: sourceParams,
            DataHex: raw.DataHex,
            HigherLowerRelevant: relevant);
    }

    private ObjectInfo? ResolveObject(uint actorId)
    {
        var localPosition = objectTable.LocalPlayer?.Position;
        foreach (var obj in objectTable)
        {
            if (obj == null)
                continue;

            if (obj.EntityId != actorId && (uint)obj.GameObjectId != actorId)
                continue;

            var native = (GameObject*)obj.Address;
            var nativePointer = (nint)native;
            var entityId = native == null ? obj.EntityId : native->EntityId;
            var layoutId = native == null ? 0u : native->LayoutId;
            var gimmickId = native == null ? 0u : native->GimmickId;
            var eventState = native == null ? 0 : native->EventState;
            var eventId = native == null ? 0u : native->EventId.Id;
            float? distance = localPosition.HasValue
                ? Vector3.Distance(localPosition.Value, obj.Position)
                : null;

            return new ObjectInfo(
                Name: obj.Name.TextValue.Trim(),
                ObjectKind: obj.ObjectKind.ToString(),
                GameObjectId: obj.GameObjectId,
                EntityId: entityId,
                BaseId: obj.BaseId,
                LayoutId: layoutId,
                GimmickId: gimmickId,
                EventState: eventState,
                EventId: eventId,
                Targetable: obj.IsTargetable,
                Position: obj.Position,
                Distance: distance,
                NativePointer: nativePointer);
        }

        return null;
    }

    private static string FormatStateData(RawServerEvent raw)
        => raw.Kind switch
        {
            ServerEventKind.EObjAnim => string.Create(
                CultureInfo.InvariantCulture,
                $"p1={raw.P1:X4} p2={raw.P2:X4} state={raw.P1:X4}{raw.P2:X4}"),
            ServerEventKind.EObjState => string.Create(
                CultureInfo.InvariantCulture,
                $"state={raw.P1:X4} p2={raw.P2:X8} housing={(raw.P3 != 0 ? raw.P4.ToString(CultureInfo.InvariantCulture) : "none")}"),
            ServerEventKind.Timeline => string.Create(CultureInfo.InvariantCulture, $"id={raw.P1:X4}"),
            ServerEventKind.MapEffect => string.Create(
                CultureInfo.InvariantCulture,
                $"index={raw.Index:X2} state={raw.State:X8} s1={raw.State1:X4} s2={raw.State2:X4}"),
            ServerEventKind.LegacyMapEffect => string.Create(
                CultureInfo.InvariantCulture,
                $"seq={raw.SequenceByte:X2} param={raw.ParamByte:X2} data={raw.DataHex}"),
            ServerEventKind.SystemLog => $"messageId={raw.MessageId} args=[{string.Join(",", raw.Args)}]",
            ServerEventKind.OpenTreasure => string.Create(CultureInfo.InvariantCulture, $"player=0x{raw.PlayerId:X8} actor=0x{raw.ActorId:X8}"),
            _ => string.Empty,
        };

    private static string FormatSourceParams(RawServerEvent raw)
        => raw.Kind switch
        {
            ServerEventKind.EObjAnim or ServerEventKind.EObjState or ServerEventKind.Timeline => string.Create(
                CultureInfo.InvariantCulture,
                $"category={raw.Category} p1=0x{raw.P1:X8} p2=0x{raw.P2:X8} p3=0x{raw.P3:X8} p4=0x{raw.P4:X8} p5=0x{raw.P5:X8} p6=0x{raw.P6:X8} p7=0x{raw.P7:X8} p8=0x{raw.P8:X8} target=0x{raw.TargetId:X} replay={raw.Replaying}"),
            ServerEventKind.MapEffect => string.Create(
                CultureInfo.InvariantCulture,
                $"source={raw.Source} index=0x{raw.Index:X2} state1=0x{raw.State1:X4} state2=0x{raw.State2:X4}"),
            ServerEventKind.LegacyMapEffect => string.Create(
                CultureInfo.InvariantCulture,
                $"eventId=0x{raw.EventId:X} seq=0x{raw.SequenceByte:X2} param=0x{raw.ParamByte:X2} length={raw.DataLength} truncated={raw.DataTruncated}"),
            ServerEventKind.SystemLog => $"entity=0x{raw.EntityId:X8} messageId={raw.MessageId} argCount={raw.ArgCount} args=[{string.Join(",", raw.Args)}]",
            ServerEventKind.OpenTreasure => $"player=0x{raw.PlayerId:X8} actor=0x{raw.ActorId:X8}",
            _ => string.Empty,
        };

    private static bool IsHigherLowerRelevant(RawServerEvent raw, ObjectInfo? objectInfo, DutyContextSnapshot context)
    {
        if (raw.Kind is ServerEventKind.LegacyMapEffect or ServerEventKind.SystemLog or ServerEventKind.OpenTreasure)
            return true;

        if (raw.Kind == ServerEventKind.MapEffect)
            return TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId);

        if (objectInfo == null)
            return false;

        if (objectInfo.BaseId == 2007457)
            return true;

        if (objectInfo.Distance is { } distance && distance <= HigherLowerNearbyRadius)
        {
            if (objectInfo.ObjectKind.Equals(ObjectKind.EventObj.ToString(), StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(objectInfo.Name)
                    || ContainsAny(objectInfo.Name, "High", "Low", "card", "lure", "lock", "treasure")))
            {
                return true;
            }
        }

        return IsHighLowName(objectInfo.Name);
    }

    private static bool IsHighLowName(string name)
        => string.Equals(name, "High", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Low", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToBossModKind(ServerEventKind kind)
        => kind switch
        {
            ServerEventKind.EObjAnim => "EANM",
            ServerEventKind.EObjState => "ESTA",
            ServerEventKind.Timeline => "PATE",
            ServerEventKind.LegacyMapEffect => "LEME",
            ServerEventKind.MapEffect => "MapEffect",
            ServerEventKind.SystemLog => "SystemLog",
            ServerEventKind.OpenTreasure => "OpenTreasure",
            _ => "Unknown",
        };

    private enum ActorControlCategory : ushort
    {
        DirectorUpdate = 109,
        PlayActionTimeline = 407,
        EObjSetState = 409,
        EObjAnimation = 413,
    }

    public enum ServerEventKind
    {
        Unknown = 0,
        EObjAnim,
        LegacyMapEffect,
        MapEffect,
        EObjState,
        Timeline,
        SystemLog,
        OpenTreasure,
    }

    private sealed record ObjectInfo(
        string Name,
        string ObjectKind,
        ulong GameObjectId,
        uint EntityId,
        uint BaseId,
        uint LayoutId,
        uint GimmickId,
        int EventState,
        uint EventId,
        bool Targetable,
        Vector3 Position,
        float? Distance,
        nint NativePointer);

    private sealed record RawServerEvent(
        DateTime TimestampUtc,
        ServerEventKind Kind,
        string Source,
        uint ActorId,
        uint EntityId,
        uint Category,
        uint P1,
        uint P2,
        uint P3,
        uint P4,
        uint P5,
        uint P6,
        uint P7,
        uint P8,
        ulong TargetId,
        byte Replaying,
        uint Index,
        ushort State1,
        ushort State2,
        uint EventId,
        byte SequenceByte,
        byte ParamByte,
        ulong DataLength,
        bool DataTruncated,
        string DataHex,
        uint PlayerId,
        uint MessageId,
        int[] Args,
        byte ArgCount)
    {
        public uint State => State1 | ((uint)State2 << 16);

        public static RawServerEvent ActorControl(
            ServerEventKind kind,
            uint actorId,
            uint category,
            uint p1,
            uint p2,
            uint p3,
            uint p4,
            uint p5,
            uint p6,
            uint p7,
            uint p8,
            ulong targetId,
            byte replaying)
            => new(
                DateTime.UtcNow,
                kind,
                "ActorControl",
                actorId,
                0,
                category,
                p1,
                p2,
                p3,
                p4,
                p5,
                p6,
                p7,
                p8,
                targetId,
                replaying,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                string.Empty,
                0,
                0,
                [],
                0);

        public static RawServerEvent MapEffect(string source, uint index, ushort state1, ushort state2)
            => new(
                DateTime.UtcNow,
                ServerEventKind.MapEffect,
                source,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                index,
                state1,
                state2,
                0,
                0,
                0,
                0,
                false,
                string.Empty,
                0,
                0,
                [],
                0);

        public static RawServerEvent LegacyMapEffect(uint eventId, byte seq, byte param, ulong length, byte[] data, bool truncated)
            => new(
                DateTime.UtcNow,
                ServerEventKind.LegacyMapEffect,
                "LegacyMapEffect",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                eventId,
                seq,
                param,
                length,
                truncated,
                Convert.ToHexString(data),
                0,
                0,
                [],
                0);

        public static RawServerEvent OpenTreasure(uint playerId, uint actorId)
            => new(
                DateTime.UtcNow,
                ServerEventKind.OpenTreasure,
                "OpenTreasure",
                actorId,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                string.Empty,
                playerId,
                0,
                [],
                0);

        public static RawServerEvent SystemLog(uint entityId, uint messageId, int[] args, byte argCount)
            => new(
                DateTime.UtcNow,
                ServerEventKind.SystemLog,
                "SystemLog",
                entityId,
                entityId,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                string.Empty,
                0,
                messageId,
                args,
                argCount);
    }

    public sealed record ServerEventRow(
        ulong Sequence,
        DateTime TimestampUtc,
        ServerEventKind Kind,
        string BossModKind,
        uint TerritoryId,
        uint MapId,
        uint ActorId,
        ulong TargetId,
        string ObjectName,
        string ObjectKind,
        ulong GameObjectId,
        uint EntityId,
        uint BaseId,
        uint LayoutId,
        uint GimmickId,
        int EventState,
        uint EventId,
        bool? Targetable,
        Vector3? Position,
        float? Distance,
        string StateData,
        string SourceParams,
        string DataHex,
        bool HigherLowerRelevant)
    {
        public string KindLabel
            => Kind switch
            {
                ServerEventKind.EObjAnim => "EObjAnim",
                ServerEventKind.LegacyMapEffect => "LegacyMapEffect",
                ServerEventKind.MapEffect => "MapEffect",
                ServerEventKind.EObjState => "EObjState",
                ServerEventKind.Timeline => "Timeline",
                ServerEventKind.SystemLog => "SystemLog",
                ServerEventKind.OpenTreasure => "OpenTreasure",
                _ => "Unknown",
            };

        public string ActorLabel
            => ActorId == 0
                ? "-"
                : string.IsNullOrWhiteSpace(ObjectName)
                    ? $"0x{ActorId:X8}"
                    : $"{ObjectName} (0x{ActorId:X8})";

        public string PositionText
            => Position.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{Position.Value.X:0.00},{Position.Value.Y:0.00},{Position.Value.Z:0.00}")
                : "-";

        public string DistanceText
            => Distance.HasValue ? Distance.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-";

        public string ToBossModLogLine()
        {
            var actor = ActorId == 0
                ? "none"
                : $"0x{ActorId:X8}";
            var name = string.IsNullOrWhiteSpace(ObjectName) ? string.Empty : $" name='{Escape(ObjectName)}'";
            var objectFields = ActorId == 0
                ? string.Empty
                : $" actor={actor}{name} objectId=0x{GameObjectId:X} entityId=0x{EntityId:X8} baseId={BaseId} kind={ObjectKind} layoutId={LayoutId} gimmickId={GimmickId} eventState={EventState} eventId=0x{EventId:X} targetable={Targetable?.ToString() ?? "unknown"}";

            return $"server-event kind={BossModKind}{objectFields} {StateData} territory={TerritoryId} map={MapId} pos={PositionText} distance={DistanceText} sourceParams=\"{Escape(SourceParams)}\"";
        }

        public bool MatchesText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            return KindLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || BossModKind.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || ObjectName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || ObjectKind.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || StateData.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || SourceParams.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || DataHex.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || ActorId.ToString("X8", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || GameObjectId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || BaseId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || LayoutId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || GimmickId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || EventId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        private static string Escape(string value)
            => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
