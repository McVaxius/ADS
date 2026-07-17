using System.Numerics;
using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

internal sealed record ShopOfflineNpcPlacement(
    uint NpcId,
    uint TerritoryId,
    string TerritoryName,
    uint MapId,
    float X,
    float Z,
    string Source,
    bool ReplaceExisting)
{
    public ShopNpcPlacementSheetRow ToSheetRow()
        => new(
            NpcId,
            TerritoryId,
            TerritoryName,
            new Vector3(X, 0, Z),
            0,
            ShopNpcPlacementSource.OfflineCatalog,
            MapId,
            $"offline-catalog:{Source}",
            RequiresFloorResolution: true,
            ReplacesExisting: ReplaceExisting);
}

internal static class ShopOfflineNpcPlacementCatalog
{
    private const int SupportedSchemaVersion = 1;
    private const string ResourceSuffix = ".Resources.shop-npc-placements.json";

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; }
        public PlacementDocument[] Placements { get; set; } = [];
    }

    private sealed class PlacementDocument
    {
        public uint NpcId { get; set; }
        public uint TerritoryId { get; set; }
        public string TerritoryName { get; set; } = string.Empty;
        public uint MapId { get; set; }
        public float X { get; set; }
        public float Z { get; set; }
        public string Source { get; set; } = string.Empty;
        public bool ReplaceExisting { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ShopOfflineNpcPlacement> LoadEmbedded()
    {
        var assembly = typeof(ShopOfflineNpcPlacementCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName == null)
            throw new InvalidDataException($"Embedded shop placement resource ending in {ResourceSuffix} was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded shop placement resource {resourceName} could not be opened.");
        return Load(stream);
    }

    public static IReadOnlyList<ShopOfflineNpcPlacement> Load(Stream stream)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("Offline shop placement catalog is empty.");
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported offline shop placement schema {document.SchemaVersion}; expected {SupportedSchemaVersion}.");
        }

        var placements = new List<ShopOfflineNpcPlacement>(document.Placements.Length);
        var identities = new HashSet<(uint NpcId, uint TerritoryId, uint MapId, float X, float Z, string Source)>();
        foreach (var row in document.Placements)
        {
            if (row.NpcId == 0
                || row.TerritoryId == 0
                || string.IsNullOrWhiteSpace(row.TerritoryName)
                || string.IsNullOrWhiteSpace(row.Source)
                || !float.IsFinite(row.X)
                || !float.IsFinite(row.Z))
            {
                throw new InvalidDataException($"Offline shop placement row for NPC {row.NpcId} is invalid.");
            }

            var source = row.Source.Trim();
            if (!identities.Add((row.NpcId, row.TerritoryId, row.MapId, row.X, row.Z, source)))
                throw new InvalidDataException($"Offline shop placement catalog contains a duplicate row for NPC {row.NpcId}.");
            placements.Add(new ShopOfflineNpcPlacement(
                row.NpcId,
                row.TerritoryId,
                row.TerritoryName.Trim(),
                row.MapId,
                row.X,
                row.Z,
                source,
                row.ReplaceExisting));
        }

        return placements
            .OrderBy(row => row.NpcId)
            .ThenBy(row => row.TerritoryId)
            .ThenBy(row => row.Source, StringComparer.Ordinal)
            .ThenBy(row => row.MapId)
            .ThenBy(row => row.X)
            .ThenBy(row => row.Z)
            .ToArray();
    }
}
