using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.GameHelpers;
using ECommons.Hooks;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ADS.Services;

public sealed unsafe class HigherLowerVfxTraceService : IDisposable
{
    private const int MaxRows = 1000;
    private const int MaxPendingRows = 4096;
    private const int MaxPathChars = 512;
    private const float HigherLowerNearbyRadius = 80f;

    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly TreasureHighLowDiagnosticService diagnostics;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly Queue<RawVfxEvent> pending = new();
    private readonly List<VfxEventRow> rows = new();
    private readonly Dictionary<nint, KnownVfx> knownVfx = new();
    private HigherLowerCardVfxSolverService? cardSolver;
    private ulong rowSequence;

    public HigherLowerVfxTraceService(
        IObjectTable objectTable,
        IClientState clientState,
        TreasureHighLowDiagnosticService diagnostics,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.diagnostics = diagnostics;
        this.log = log;

        ActorVfx.ActorVfxCreateEvent += OnActorVfxCreate;
        ActorVfx.ActorVfxDtorEvent += OnActorVfxDtor;
        StaticVfx.StaticVfxCreateEvent += OnStaticVfxCreate;
        StaticVfx.StaticVfxRunEvent += OnStaticVfxRun;
        StaticVfx.StaticVfxDtorEvent += OnStaticVfxDtor;
    }

    public void AttachCardSolver(HigherLowerCardVfxSolverService solver)
        => cardSolver = solver;

    public int PendingCount
    {
        get
        {
            lock (gate)
                return pending.Count;
        }
    }

    public IReadOnlyList<VfxEventRow> GetRowsSnapshot()
    {
        lock (gate)
            return rows.ToList();
    }

    public IReadOnlyList<TrackedVfxRow> GetTrackedSnapshot(DutyContextSnapshot context)
    {
        List<VfxInfo> tracked;
        try
        {
            tracked = VfxManager.TrackedEffects.ToList();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Failed to snapshot tracked VFX list.");
            return [];
        }

        var result = new List<TrackedVfxRow>(tracked.Count);
        foreach (var effect in tracked)
        {
            if (effect.Placement == null)
                continue;

            var path = effect.Path ?? string.Empty;
            var caster = ResolveObject(effect.CasterID);
            var target = ResolveObject(effect.TargetID);
            var distance = GetDistance(effect.Placement.Position);
            var relevant = IsHigherLowerRelevant(
                effect.IsStatic,
                path,
                caster,
                target,
                context,
                distance);
            var cardProbe = cardSolver?.FindTrackedProbe(path, effect.VfxID, effect.Placement.Position);
            if (cardProbe == null && IsKnownCardVfxPath(path))
                cardProbe = cardSolver?.BuildProbe(path, (nint)effect.VfxID, effect.Placement.Position, DateTime.UtcNow, context);
            var cardFields = BuildTrackedCardFields(path, cardProbe);

            result.Add(new TrackedVfxRow(
                Path: path,
                VfxId: effect.VfxID,
                CasterId: effect.CasterID,
                TargetId: effect.TargetID,
                CasterLabel: BuildObjectLabel(effect.CasterID, caster),
                TargetLabel: BuildObjectLabel(effect.TargetID, target),
                TerritoryId: context.TerritoryTypeId != 0 ? context.TerritoryTypeId : clientState.TerritoryType,
                MapId: context.MapId,
                Position: effect.Placement.Position,
                Scale: effect.Placement.Scale,
                Rotation: new VfxRotation(effect.Placement.Rotation.X, effect.Placement.Rotation.Y, effect.Placement.Rotation.Z, effect.Placement.Rotation.W),
                Distance: distance,
                AgeSeconds: effect.AgeSeconds,
                IsStatic: effect.IsStatic,
                HasRun: effect.HasRun,
                HigherLowerRelevant: relevant,
                CardSource: cardFields.CardSource,
                Slot: cardFields.Slot,
                TextureIndex: cardFields.TextureIndex,
                DecodedCard: cardFields.DecodedCard,
                SolverReason: cardFields.SolverReason));
        }

        return result
            .OrderByDescending(static x => x.HigherLowerRelevant)
            .ThenBy(static x => x.AgeSeconds)
            .ThenBy(static x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        List<RawVfxEvent> drained;
        lock (gate)
        {
            if (pending.Count == 0)
                return;

            drained = pending.ToList();
            pending.Clear();
        }

        foreach (var raw in drained)
        {
            var (row, probe) = BuildRow(raw, context);
            lock (gate)
            {
                rows.Add(row);
                while (rows.Count > MaxRows)
                    rows.RemoveAt(0);
            }

            diagnostics.RecordVfxEvent(row);
            if (probe != null)
                cardSolver?.RecordProbe(probe);
        }
    }

    public void Dispose()
    {
        ActorVfx.ActorVfxCreateEvent -= OnActorVfxCreate;
        ActorVfx.ActorVfxDtorEvent -= OnActorVfxDtor;
        StaticVfx.StaticVfxCreateEvent -= OnStaticVfxCreate;
        StaticVfx.StaticVfxRunEvent -= OnStaticVfxRun;
        StaticVfx.StaticVfxDtorEvent -= OnStaticVfxDtor;
    }

    private void OnActorVfxCreate(nint vfxPtr, nint vfxPathPtr, nint casterAddress, nint targetAddress, float a4, byte a5, ushort a6, byte a7)
    {
        try
        {
            var snapshot = ReadVfxSnapshot(vfxPtr);
            var raw = RawVfxEvent.Create(
                VfxEventKind.ActorCreate,
                vfxPtr,
                ReadPath(vfxPathPtr),
                snapshot.ActorCasterId,
                snapshot.ActorTargetId,
                snapshot.Position,
                snapshot.Scale,
                snapshot.Rotation,
                isStatic: false,
                hasRun: true,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"casterAddress=0x{casterAddress:X} targetAddress=0x{targetAddress:X} a4={a4:0.###} a5={a5} a6={a6} a7={a7} actorCaster=0x{snapshot.ActorCasterId:X} actorTarget=0x{snapshot.ActorTargetId:X}"));
            Enqueue(raw);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Actor VFX create callback failed.");
        }
    }

    private void OnActorVfxDtor(nint actorVfxAddress)
    {
        try
        {
            var snapshot = ReadVfxSnapshot(actorVfxAddress);
            var raw = RawVfxEvent.Create(
                VfxEventKind.ActorDtor,
                actorVfxAddress,
                string.Empty,
                snapshot.ActorCasterId,
                snapshot.ActorTargetId,
                snapshot.Position,
                snapshot.Scale,
                snapshot.Rotation,
                isStatic: false,
                hasRun: true,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"actorCaster=0x{snapshot.ActorCasterId:X} actorTarget=0x{snapshot.ActorTargetId:X}"));
            Enqueue(raw, removeKnownAfterEnqueue: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Actor VFX dtor callback failed.");
        }
    }

    private void OnStaticVfxCreate(nint vfxPtr, string path, string systemSource)
    {
        try
        {
            var snapshot = ReadVfxSnapshot(vfxPtr);
            var raw = RawVfxEvent.Create(
                VfxEventKind.StaticCreate,
                vfxPtr,
                path,
                snapshot.StaticCasterId,
                snapshot.StaticTargetId,
                snapshot.Position,
                snapshot.Scale,
                snapshot.Rotation,
                isStatic: true,
                hasRun: false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"systemSource='{Escape(systemSource ?? string.Empty)}' staticCaster=0x{snapshot.StaticCasterId:X} staticTarget=0x{snapshot.StaticTargetId:X}"));
            Enqueue(raw);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Static VFX create callback failed.");
        }
    }

    private void OnStaticVfxRun(nint staticVfxAddress, float a1, uint a2)
    {
        try
        {
            var snapshot = ReadVfxSnapshot(staticVfxAddress);
            var raw = RawVfxEvent.Create(
                VfxEventKind.StaticRun,
                staticVfxAddress,
                string.Empty,
                snapshot.StaticCasterId,
                snapshot.StaticTargetId,
                snapshot.Position,
                snapshot.Scale,
                snapshot.Rotation,
                isStatic: true,
                hasRun: true,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"a1={a1:0.###} a2=0x{a2:X8} staticCaster=0x{snapshot.StaticCasterId:X} staticTarget=0x{snapshot.StaticTargetId:X}"));
            Enqueue(raw);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Static VFX run callback failed.");
        }
    }

    private void OnStaticVfxDtor(nint staticVfxAddress)
    {
        try
        {
            var snapshot = ReadVfxSnapshot(staticVfxAddress);
            var raw = RawVfxEvent.Create(
                VfxEventKind.StaticDtor,
                staticVfxAddress,
                string.Empty,
                snapshot.StaticCasterId,
                snapshot.StaticTargetId,
                snapshot.Position,
                snapshot.Scale,
                snapshot.Rotation,
                isStatic: true,
                hasRun: true,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"staticCaster=0x{snapshot.StaticCasterId:X} staticTarget=0x{snapshot.StaticTargetId:X}"));
            Enqueue(raw, removeKnownAfterEnqueue: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][VFX] Static VFX dtor callback failed.");
        }
    }

    private void Enqueue(RawVfxEvent raw, bool removeKnownAfterEnqueue = false)
    {
        lock (gate)
        {
            if (raw.Pointer != nint.Zero && knownVfx.TryGetValue(raw.Pointer, out var known))
                raw = raw with
                {
                    Path = string.IsNullOrWhiteSpace(raw.Path) ? known.Path : raw.Path,
                    CasterId = raw.CasterId == 0 ? known.CasterId : raw.CasterId,
                    TargetId = raw.TargetId == 0 ? known.TargetId : raw.TargetId,
                    Position = raw.Position == Vector3.Zero ? known.Position : raw.Position,
                    Scale = raw.Scale == Vector3.Zero ? known.Scale : raw.Scale,
                    Rotation = raw.Rotation.IsZero ? known.Rotation : raw.Rotation,
                };

            pending.Enqueue(raw);
            while (pending.Count > MaxPendingRows)
                pending.Dequeue();

            if (raw.Pointer != nint.Zero)
            {
                if (removeKnownAfterEnqueue)
                {
                    knownVfx.Remove(raw.Pointer);
                }
                else
                {
                    knownVfx[raw.Pointer] = new KnownVfx(
                        raw.Path,
                        raw.CasterId,
                        raw.TargetId,
                        raw.IsStatic,
                        raw.Position,
                        raw.Scale,
                        raw.Rotation);
                }
            }
        }
    }

    private (VfxEventRow Row, HigherLowerCardVfxSolverService.CardProbeRow? Probe) BuildRow(RawVfxEvent raw, DutyContextSnapshot context)
    {
        var caster = ResolveObject(raw.CasterId);
        var target = ResolveObject(raw.TargetId);
        var distance = GetDistance(raw.Position);
        var relevant = IsHigherLowerRelevant(raw.IsStatic, raw.Path, caster, target, context, distance);
        var probe = cardSolver?.BuildProbe(raw.Path, raw.Pointer, raw.Position, raw.TimestampUtc, context);

        var row = new VfxEventRow(
            Sequence: ++rowSequence,
            TimestampUtc: raw.TimestampUtc,
            Kind: raw.Kind,
            Path: raw.Path,
            Pointer: raw.Pointer,
            CasterId: raw.CasterId,
            TargetId: raw.TargetId,
            CasterLabel: BuildObjectLabel(raw.CasterId, caster),
            TargetLabel: BuildObjectLabel(raw.TargetId, target),
            CasterName: caster?.Name ?? string.Empty,
            TargetName: target?.Name ?? string.Empty,
            CasterBaseId: caster?.BaseId ?? 0,
            TargetBaseId: target?.BaseId ?? 0,
            TerritoryId: context.TerritoryTypeId != 0 ? context.TerritoryTypeId : clientState.TerritoryType,
            MapId: context.MapId,
            Position: raw.Position,
            Scale: raw.Scale,
            Rotation: raw.Rotation,
            Distance: distance,
            IsStatic: raw.IsStatic,
            HasRun: raw.HasRun,
            HigherLowerRelevant: relevant,
            SourceParams: raw.SourceParams,
            CardSource: probe?.CardSource ?? string.Empty,
            Slot: probe?.Slot ?? string.Empty,
            TextureIndex: probe?.TextureIndex,
            DecodedCard: probe?.DecodedCard,
            SolverReason: probe?.Reason ?? string.Empty);

        return (row, probe);
    }

    private static CardFields BuildTrackedCardFields(string path, HigherLowerCardVfxSolverService.CardProbeRow? probe)
    {
        if (probe != null)
        {
            return new CardFields(
                probe.CardSource,
                probe.Slot,
                probe.TextureIndex,
                probe.DecodedCard,
                probe.Reason);
        }

        var normalizedPath = HigherLowerCardVfxCatalog.NormalizePath(path);
        if (HigherLowerCardVfxCatalog.IsEffectOnly(normalizedPath))
            return new CardFields(HigherLowerCardVfxSolverService.AvfxTexturePathSource, "unknown", null, null, "effect-only-card-vfx");

        if (HigherLowerCardVfxCatalog.TryGetCatalog(normalizedPath, out _))
            return new CardFields(HigherLowerCardVfxSolverService.AvfxTexturePathSource, "unknown", null, null, "no-card-texture-path");

        return CardFields.Empty;
    }

    private static bool IsKnownCardVfxPath(string path)
    {
        var normalizedPath = HigherLowerCardVfxCatalog.NormalizePath(path);
        return HigherLowerCardVfxCatalog.IsEffectOnly(normalizedPath)
               || HigherLowerCardVfxCatalog.TryGetCatalog(normalizedPath, out _);
    }

    private ObjectInfo? ResolveObject(ulong objectId)
    {
        if (objectId == 0)
            return null;

        var localPosition = objectTable.LocalPlayer?.Position;
        foreach (var obj in objectTable)
        {
            if (obj == null)
                continue;

            if (obj.GameObjectId != objectId
                && obj.EntityId != objectId
                && (uint)obj.GameObjectId != (uint)objectId)
            {
                continue;
            }

            var native = (GameObject*)obj.Address;
            var layoutId = native == null ? 0u : native->LayoutId;
            var gimmickId = native == null ? 0u : native->GimmickId;
            var eventState = native == null ? 0 : native->EventState;
            var eventId = native == null ? 0u : native->EventId.Id;
            var distance = localPosition.HasValue
                ? Vector3.Distance(localPosition.Value, obj.Position)
                : (float?)null;

            return new ObjectInfo(
                Name: obj.Name.TextValue.Trim(),
                ObjectKind: obj.ObjectKind,
                GameObjectId: obj.GameObjectId,
                EntityId: obj.EntityId,
                BaseId: obj.BaseId,
                LayoutId: layoutId,
                GimmickId: gimmickId,
                EventState: eventState,
                EventId: eventId,
                Targetable: obj.IsTargetable,
                Position: obj.Position,
                Distance: distance);
        }

        return null;
    }

    private float? GetDistance(Vector3 position)
    {
        if (objectTable.LocalPlayer == null || position == Vector3.Zero)
            return null;

        return Vector3.Distance(objectTable.LocalPlayer.Position, position);
    }

    private static bool IsHigherLowerRelevant(
        bool isStatic,
        string path,
        ObjectInfo? caster,
        ObjectInfo? target,
        DutyContextSnapshot context,
        float? distance)
    {
        if (IsHigherLowerObject(caster) || IsHigherLowerObject(target))
            return true;

        if (!TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId))
            return false;

        if (isStatic && distance is { } staticDistance && staticDistance <= HigherLowerNearbyRadius)
            return true;

        return ContainsAny(path, "high", "low", "card", "treasure", "lure");
    }

    private static bool IsHigherLowerObject(ObjectInfo? info)
    {
        if (info == null)
            return false;

        if (info.BaseId == 2007457 || IsHighLowName(info.Name))
            return true;

        return info.Distance is { } distance
               && distance <= HigherLowerNearbyRadius
               && info.ObjectKind == ObjectKind.EventObj
               && (string.IsNullOrWhiteSpace(info.Name)
                   || ContainsAny(info.Name, "High", "Low", "card", "lure", "lock", "treasure"));
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

    private static string BuildObjectLabel(ulong id, ObjectInfo? info)
    {
        if (id == 0)
            return "-";

        if (info == null)
            return $"0x{id:X}";

        return string.IsNullOrWhiteSpace(info.Name)
            ? $"0x{id:X} base={info.BaseId}"
            : $"{info.Name} (0x{id:X}, base={info.BaseId})";
    }

    private static VfxStructSnapshot ReadVfxSnapshot(nint vfxPtr)
    {
        if (vfxPtr == nint.Zero)
            return VfxStructSnapshot.Empty;

        var vfx = (VfxStruct*)vfxPtr;
        return new VfxStructSnapshot(
            Position: vfx->Position,
            Scale: vfx->Scale,
            Rotation: new VfxRotation(vfx->Rotation.X, vfx->Rotation.Y, vfx->Rotation.Z, vfx->Rotation.W),
            ActorCasterId: vfx->ActorCasterID,
            ActorTargetId: vfx->ActorTargetID,
            StaticCasterId: vfx->StaticCasterID,
            StaticTargetId: vfx->StaticTargetID);
    }

    private static string ReadPath(nint pathPtr)
    {
        if (pathPtr == nint.Zero)
            return string.Empty;

        var value = Marshal.PtrToStringAnsi(pathPtr) ?? string.Empty;
        value = value.TrimEnd('\0');
        return value.Length <= MaxPathChars ? value : value[..MaxPathChars];
    }

    private static string Format(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    private static string FormatId(ulong value)
        => value == 0 ? "none" : $"0x{value:X}";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    public enum VfxEventKind
    {
        ActorCreate,
        ActorDtor,
        StaticCreate,
        StaticRun,
        StaticDtor,
    }

    public readonly record struct VfxRotation(float X, float Y, float Z, float W)
    {
        public bool IsZero
            => X == 0f && Y == 0f && Z == 0f && W == 0f;

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture, $"{X:0.###},{Y:0.###},{Z:0.###},{W:0.###}");
    }

    public sealed record VfxEventRow(
        ulong Sequence,
        DateTime TimestampUtc,
        VfxEventKind Kind,
        string Path,
        nint Pointer,
        ulong CasterId,
        ulong TargetId,
        string CasterLabel,
        string TargetLabel,
        string CasterName,
        string TargetName,
        uint CasterBaseId,
        uint TargetBaseId,
        uint TerritoryId,
        uint MapId,
        Vector3 Position,
        Vector3 Scale,
        VfxRotation Rotation,
        float? Distance,
        bool IsStatic,
        bool HasRun,
        bool HigherLowerRelevant,
        string SourceParams,
        string CardSource,
        string Slot,
        int? TextureIndex,
        int? DecodedCard,
        string SolverReason)
    {
        public string KindLabel
            => Kind.ToString();

        public string PositionText
            => Position == Vector3.Zero ? "-" : Format(Position);

        public string DistanceText
            => Distance.HasValue ? Distance.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-";

        public string ScaleRotationText
            => $"scale={Format(Scale)} rot={Rotation}";

        public string TextureIndexText
            => TextureIndex.HasValue ? TextureIndex.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

        public string DecodedCardText
            => DecodedCard.HasValue ? DecodedCard.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

        public string ToHldbgLogLine()
            => $"vfx kind={KindLabel} path='{Escape(Path)}' ptr=0x{Pointer:X} caster={FormatId(CasterId)} target={FormatId(TargetId)} " +
               $"casterBaseId={CasterBaseId} targetBaseId={TargetBaseId} pos={PositionText} distance={DistanceText} scale={Format(Scale)} rotation={Rotation} " +
               $"territory={TerritoryId} map={MapId} isStatic={IsStatic} hasRun={HasRun} sourceParams=\"{Escape(SourceParams)}\"";

        public bool MatchesText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            return KindLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || CasterLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || TargetLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || CasterName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || TargetName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || CardSource.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || Slot.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || SolverReason.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || SourceParams.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || TextureIndexText.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || DecodedCardText.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || Pointer.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || CasterId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || TargetId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || CasterBaseId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                   || TargetBaseId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed record TrackedVfxRow(
        string Path,
        long VfxId,
        ulong CasterId,
        ulong TargetId,
        string CasterLabel,
        string TargetLabel,
        uint TerritoryId,
        uint MapId,
        Vector3 Position,
        Vector3 Scale,
        VfxRotation Rotation,
        float? Distance,
        float AgeSeconds,
        bool IsStatic,
        bool HasRun,
        bool HigherLowerRelevant,
        string CardSource,
        string Slot,
        int? TextureIndex,
        int? DecodedCard,
        string SolverReason)
    {
        public string PositionText
            => Position == Vector3.Zero ? "-" : Format(Position);

        public string DistanceText
            => Distance.HasValue ? Distance.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-";

        public string ScaleRotationText
            => $"scale={Format(Scale)} rot={Rotation}";

        public string TextureIndexText
            => TextureIndex.HasValue ? TextureIndex.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

        public string DecodedCardText
            => DecodedCard.HasValue ? DecodedCard.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

    private readonly record struct CardFields(
        string CardSource,
        string Slot,
        int? TextureIndex,
        int? DecodedCard,
        string SolverReason)
    {
        public static CardFields Empty { get; } = new(string.Empty, string.Empty, null, null, string.Empty);
    }

    private sealed record RawVfxEvent(
        DateTime TimestampUtc,
        VfxEventKind Kind,
        nint Pointer,
        string Path,
        ulong CasterId,
        ulong TargetId,
        Vector3 Position,
        Vector3 Scale,
        VfxRotation Rotation,
        bool IsStatic,
        bool HasRun,
        string SourceParams)
    {
        public static RawVfxEvent Create(
            VfxEventKind kind,
            nint pointer,
            string path,
            ulong casterId,
            ulong targetId,
            Vector3 position,
            Vector3 scale,
            VfxRotation rotation,
            bool isStatic,
            bool hasRun,
            string sourceParams)
            => new(
                DateTime.UtcNow,
                kind,
                pointer,
                path ?? string.Empty,
                casterId,
                targetId,
                position,
                scale,
                rotation,
                isStatic,
                hasRun,
                sourceParams);
    }

    private sealed record KnownVfx(
        string Path,
        ulong CasterId,
        ulong TargetId,
        bool IsStatic,
        Vector3 Position,
        Vector3 Scale,
        VfxRotation Rotation);

    private sealed record VfxStructSnapshot(
        Vector3 Position,
        Vector3 Scale,
        VfxRotation Rotation,
        ulong ActorCasterId,
        ulong ActorTargetId,
        ulong StaticCasterId,
        ulong StaticTargetId)
    {
        public static VfxStructSnapshot Empty { get; } = new(
            Vector3.Zero,
            Vector3.Zero,
            default,
            0,
            0,
            0,
            0);
    }

    private sealed record ObjectInfo(
        string Name,
        ObjectKind ObjectKind,
        ulong GameObjectId,
        uint EntityId,
        uint BaseId,
        uint LayoutId,
        uint GimmickId,
        int EventState,
        uint EventId,
        bool Targetable,
        Vector3 Position,
        float? Distance);
}
