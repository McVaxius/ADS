using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ExplorerSnapshotExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly string exportDirectory;

    public ExplorerSnapshotExportService(IObjectTable objectTable, IPluginLog log, string configDirectory)
    {
        this.objectTable = objectTable;
        this.log = log;
        exportDirectory = Path.Combine(configDirectory, "ExplorerExports");
    }

    public string ExportDirectory
        => exportDirectory;

    public string LastExportPath { get; private set; } = string.Empty;

    public string Status { get; private set; } = "No explorer snapshot exported.";

    public ExplorerSnapshotExportResult Export(
        string pluginVersion,
        DutyContextSnapshot context,
        IReadOnlyList<HigherLowerServerEventTraceService.ServerEventRow> serverEvents)
    {
        try
        {
            var exportedAtUtc = DateTime.UtcNow;
            var objects = CaptureObjectTable();
            var document = new ExplorerSnapshotDocument(
                FormatVersion: 1,
                ExportedAtUtc: exportedAtUtc,
                PluginVersion: pluginVersion,
                Context: new ExplorerContextSnapshot(
                    context.TerritoryTypeId,
                    context.MapId,
                    context.ContentFinderConditionId,
                    context.IsLoggedIn,
                    context.InInstancedDuty,
                    context.Mounted),
                ObjectTableLength: objectTable.Length,
                LoadedObjectCount: objects.Count,
                ServerEventCount: serverEvents.Count,
                Objects: objects,
                ServerEvents: serverEvents);

            Directory.CreateDirectory(exportDirectory);
            var path = GetAvailablePath(exportedAtUtc);
            WriteDocument(path, document);

            LastExportPath = path;
            Status = $"Exported {serverEvents.Count} events and {objects.Count} objects to {path}";
            log.Information($"[ADS][ExplorerExport] {Status}");
            return new ExplorerSnapshotExportResult(true, path, Status);
        }
        catch (Exception ex)
        {
            Status = $"Explorer snapshot export failed: {ex.Message}";
            log.Warning(ex, "[ADS][ExplorerExport] Snapshot export failed.");
            return new ExplorerSnapshotExportResult(false, string.Empty, Status);
        }
    }

    internal static void WriteDocument(string path, ExplorerSnapshotDocument document)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, document, JsonOptions);
    }

    private List<ExplorerObjectSnapshot> CaptureObjectTable()
    {
        var localPlayerId = objectTable.LocalPlayer?.GameObjectId;
        var result = new List<ExplorerObjectSnapshot>();
        for (var index = 0; index < objectTable.Length; index++)
        {
            var gameObject = objectTable[index];
            if (gameObject is null)
                continue;

            var position = gameObject.Position;
            result.Add(new ExplorerObjectSnapshot(
                TableIndex: gameObject.ObjectIndex,
                Name: gameObject.Name.TextValue,
                GameObjectId: gameObject.GameObjectId,
                GameObjectIdHex: $"0x{gameObject.GameObjectId:X}",
                EntityId: gameObject.EntityId,
                EntityIdHex: $"0x{gameObject.EntityId:X8}",
                DataId: gameObject.BaseId,
                BaseId: gameObject.BaseId,
                OwnerId: gameObject.OwnerId,
                OwnerIdHex: $"0x{gameObject.OwnerId:X8}",
                ObjectKind: gameObject.ObjectKind.ToString(),
                SubKind: gameObject.SubKind,
                YalmDistanceX: gameObject.YalmDistanceX,
                YalmDistanceZ: gameObject.YalmDistanceZ,
                IsDead: gameObject.IsDead,
                IsTargetable: gameObject.IsTargetable,
                IsLocalPlayer: localPlayerId.HasValue && localPlayerId.Value == gameObject.GameObjectId,
                Position: new ExplorerPositionSnapshot(position.X, position.Y, position.Z),
                Rotation: gameObject.Rotation,
                HitboxRadius: gameObject.HitboxRadius,
                TargetObjectId: gameObject.TargetObjectId,
                TargetObjectIdHex: $"0x{gameObject.TargetObjectId:X}",
                AddressHex: string.Create(CultureInfo.InvariantCulture, $"0x{gameObject.Address.ToInt64():X}")));
        }

        return result;
    }

    private string GetAvailablePath(DateTime exportedAtUtc)
    {
        var stem = $"ADS_explorer_snapshot_{exportedAtUtc:yyyyMMdd_HHmmss_fff}";
        for (var suffix = 0; ; suffix++)
        {
            var fileName = suffix == 0 ? $"{stem}.json" : $"{stem}_{suffix}.json";
            var path = Path.Combine(exportDirectory, fileName);
            if (!File.Exists(path))
                return path;
        }
    }
}

public sealed record ExplorerSnapshotExportResult(bool Success, string Path, string Status);

internal sealed record ExplorerSnapshotDocument(
    int FormatVersion,
    DateTime ExportedAtUtc,
    string PluginVersion,
    ExplorerContextSnapshot Context,
    int ObjectTableLength,
    int LoadedObjectCount,
    int ServerEventCount,
    IReadOnlyList<ExplorerObjectSnapshot> Objects,
    IReadOnlyList<HigherLowerServerEventTraceService.ServerEventRow> ServerEvents);

internal sealed record ExplorerContextSnapshot(
    uint TerritoryTypeId,
    uint MapId,
    uint ContentFinderConditionId,
    bool IsLoggedIn,
    bool InInstancedDuty,
    bool Mounted);

internal sealed record ExplorerObjectSnapshot(
    ushort TableIndex,
    string Name,
    ulong GameObjectId,
    string GameObjectIdHex,
    uint EntityId,
    string EntityIdHex,
    uint DataId,
    uint BaseId,
    uint OwnerId,
    string OwnerIdHex,
    string ObjectKind,
    byte SubKind,
    float YalmDistanceX,
    float YalmDistanceZ,
    bool IsDead,
    bool IsTargetable,
    bool IsLocalPlayer,
    ExplorerPositionSnapshot Position,
    float Rotation,
    float HitboxRadius,
    ulong TargetObjectId,
    string TargetObjectIdHex,
    string AddressHex);

internal sealed record ExplorerPositionSnapshot(float X, float Y, float Z);
