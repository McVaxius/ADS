using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace ADS.Services;

internal interface IShopCatalog
{
    ShopCatalogResolution Resolve(uint itemId, int quantity);
}

internal interface IShopSheetSource
{
    ShopCatalogSnapshot BuildSnapshot();
}

internal sealed class ShopCatalogService(
    IShopSheetSource sheetSource,
    Action<string>? diagnostic = null) : IShopCatalog
{
    private readonly object sync = new();
    private ShopCatalogSnapshot? snapshot;

    public ShopCatalogResolution Resolve(uint itemId, int quantity)
    {
        var current = snapshot;
        if (current == null)
        {
            lock (sync)
                current = snapshot ??= sheetSource.BuildSnapshot();
        }

        return ShopCatalogBuilder.Resolve(current, itemId, quantity, diagnostic);
    }
}

internal static class ShopAetheryteRouteBuilder
{
    private const byte AetheryteMarkerDataType = 3;
    private const float MapMarkerOrigin = 1024f;

    public static ShopAetheryteBuildResult Build(
        IReadOnlyList<ShopAetheryteSourceRow> aetherytes,
        IReadOnlyList<ShopMapSheetRow> maps,
        IReadOnlyList<ShopMapMarkerSheetRow> markers)
    {
        var routes = new List<ShopAetheryteSheetRow>();
        var rejected = 0;
        var missing = 0;

        foreach (var aetheryte in aetherytes.Where(row => row.IsAetheryte).OrderBy(row => row.AetheryteId))
        {
            if (aetheryte.MapId == 0)
            {
                missing++;
                continue;
            }

            var matchingMaps = maps.Where(map => map.MapId == aetheryte.MapId).ToArray();
            if (matchingMaps.Length == 0)
            {
                missing++;
                continue;
            }

            if (matchingMaps.Length != 1)
            {
                rejected++;
                continue;
            }

            var map = matchingMaps[0];
            if (map.TerritoryId == 0
                || map.TerritoryId != aetheryte.TerritoryId
                || map.MapMarkerRange == 0
                || map.SizeFactor == 0)
            {
                rejected++;
                continue;
            }

            var matchingMarkers = markers
                .Where(marker => marker.MapMarkerRange == map.MapMarkerRange
                    && marker.DataType == AetheryteMarkerDataType
                    && marker.DataKey == aetheryte.AetheryteId)
                .ToArray();
            if (matchingMarkers.Length == 0)
            {
                missing++;
                continue;
            }

            if (matchingMarkers.Length != 1)
            {
                rejected++;
                continue;
            }

            var marker = matchingMarkers[0];
            routes.Add(new ShopAetheryteSheetRow(
                aetheryte.AetheryteId,
                string.IsNullOrWhiteSpace(aetheryte.Name) ? $"Aetheryte {aetheryte.AetheryteId}" : aetheryte.Name,
                aetheryte.TerritoryId,
                ConvertMarkerToWorld(marker.X, marker.Y, map.SizeFactor, map.OffsetX, map.OffsetY)));
        }

        return new ShopAetheryteBuildResult(routes, routes.Count, rejected, missing);
    }

    public static Vector3 ConvertMarkerToWorld(
        short markerX,
        short markerY,
        ushort sizeFactor,
        short offsetX,
        short offsetY)
    {
        if (sizeFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(sizeFactor), "Map size factor must be nonzero.");

        var scale = sizeFactor / 100f;
        return new Vector3(
            ((markerX - MapMarkerOrigin) / scale) - offsetX,
            0,
            ((markerY - MapMarkerOrigin) / scale) - offsetY);
    }
}

internal static class ShopLgbPathBuilder
{
    private const string LevelSegment = "/level/";

    public static bool TryDerivePlaneventPath(string? backgroundPath, out string lgbPath)
    {
        lgbPath = string.Empty;
        if (string.IsNullOrWhiteSpace(backgroundPath))
            return false;

        var normalized = backgroundPath.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("bg/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..];
        var levelIndex = normalized.IndexOf(LevelSegment, StringComparison.OrdinalIgnoreCase);
        if (levelIndex <= 0)
            return false;

        lgbPath = $"bg/{normalized[..(levelIndex + 1)]}level/planevent.lgb";
        return true;
    }
}

internal static class ShopNpcLinkBuilder
{
    public static IReadOnlyList<ShopNpcSheetLink> Build(
        IReadOnlySet<uint> validGilShopIds,
        IReadOnlySet<uint> validSpecialShopIds,
        IReadOnlyList<ShopNpcEventSheetRow> npcs,
        IReadOnlyList<ShopTopicSelectSheetRow> topics,
        IReadOnlyList<ShopPreHandlerSheetRow> preHandlers,
        IReadOnlySet<uint>? validGrandCompanyShopIds = null,
        IReadOnlySet<uint>? validFreeCompanyShopIds = null,
        IReadOnlyList<ShopFateShopSheetRow>? fateShops = null,
        IReadOnlyList<ShopInclusionRouteSheetRow>? inclusionRoutes = null,
        IReadOnlyList<ShopCustomTalkSheetRow>? customTalks = null)
    {
        var topicsById = topics
            .GroupBy(row => row.TopicSelectId)
            .ToDictionary(group => group.Key, group => group.First());
        var preHandlersById = preHandlers
            .GroupBy(row => row.PreHandlerId)
            .ToDictionary(group => group.Key, group => group.First());
        var fateShopsById = (fateShops ?? [])
            .GroupBy(row => row.FateShopId)
            .ToDictionary(group => group.Key, group => group.First());
        var inclusionRoutesById = (inclusionRoutes ?? [])
            .GroupBy(row => row.InclusionShopId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Page).ThenBy(row => row.Subpage).ToArray());
        var customTalksById = (customTalks ?? [])
            .GroupBy(row => row.CustomTalkId)
            .ToDictionary(group => group.Key, group => group.First());
        validGrandCompanyShopIds ??= new HashSet<uint>();
        validFreeCompanyShopIds ??= new HashSet<uint>();
        var links = new List<ShopNpcSheetLink>();

        foreach (var npc in npcs.OrderBy(row => row.NpcId))
        {
            for (var eventIndex = 0; eventIndex < npc.Events.Count; eventIndex++)
            {
                var npcEvent = npc.Events[eventIndex];
                var npcStep = new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, eventIndex, npcEvent.RowId);
                Traverse(
                    npc,
                    npcEvent,
                    [npcStep],
                    [],
                    false,
                    viaTopicSelect: false,
                    throughPreHandler: false,
                    viaCustomTalk: false,
                    new HashSet<(ShopNpcEventKind, uint)>());
            }
        }

        return links;

        void Traverse(
            ShopNpcEventSheetRow npc,
            ShopNpcEventReference current,
            IReadOnlyList<ShopMenuPathStep> path,
            IReadOnlyList<uint> requiredQuests,
            bool hasUnknownGate,
            bool viaTopicSelect,
            bool throughPreHandler,
            bool viaCustomTalk,
            HashSet<(ShopNpcEventKind, uint)> visited)
        {
            if (!visited.Add((current.Kind, current.RowId)))
                return;

            switch (current.Kind)
            {
                case ShopNpcEventKind.PreHandler when preHandlersById.TryGetValue(current.RowId, out var preHandler):
                    Traverse(
                        npc,
                        preHandler.Target,
                        path,
                        requiredQuests.Concat(preHandler.UnlockQuestId == 0 ? [] : [preHandler.UnlockQuestId]).Distinct().ToArray(),
                        hasUnknownGate || preHandler.HasUnknownGate,
                        viaTopicSelect,
                        true,
                        viaCustomTalk,
                        new HashSet<(ShopNpcEventKind, uint)>(visited));
                    return;
                case ShopNpcEventKind.TopicSelect when topicsById.TryGetValue(current.RowId, out var topic):
                    for (var index = 0; index < topic.Shops.Count; index++)
                    {
                        var target = topic.Shops[index];
                        Traverse(
                            npc,
                            target,
                            path.Concat([new ShopMenuPathStep(ShopMenuPathStepKind.TopicSelectShop, index, target.RowId)]).ToArray(),
                            requiredQuests,
                            hasUnknownGate,
                            true,
                            throughPreHandler,
                            viaCustomTalk,
                            new HashSet<(ShopNpcEventKind, uint)>(visited));
                    }
                    return;
                case ShopNpcEventKind.CustomTalk when customTalksById.TryGetValue(current.RowId, out var customTalk):
                    for (var index = 0; index < customTalk.Links.Count; index++)
                    {
                        var target = customTalk.Links[index];
                        Traverse(
                            npc,
                            target,
                            path.Concat([new ShopMenuPathStep(ShopMenuPathStepKind.CustomTalkSpecialLink, index, target.RowId)]).ToArray(),
                            requiredQuests,
                            hasUnknownGate,
                            viaTopicSelect,
                            throughPreHandler,
                            true,
                            new HashSet<(ShopNpcEventKind, uint)>(visited));
                    }
                    return;
                case ShopNpcEventKind.FateShop when fateShopsById.TryGetValue(current.RowId, out var fateShop):
                    foreach (var shopId in fateShop.SpecialShopIds.Where(validSpecialShopIds.Contains).Distinct())
                        AddTerminal(ShopSheetKind.Special, shopId, ShopNpcLinkKind.FateShop, path, requiredQuests, hasUnknownGate);
                    return;
                case ShopNpcEventKind.InclusionShop when inclusionRoutesById.TryGetValue(current.RowId, out var routes):
                    foreach (var route in routes.Where(route => validSpecialShopIds.Contains(route.SpecialShopId)))
                    {
                        AddTerminal(
                            ShopSheetKind.Special,
                            route.SpecialShopId,
                            ShopNpcLinkKind.InclusionShop,
                            path.Concat([
                                new ShopMenuPathStep(ShopMenuPathStepKind.InclusionPage, route.Page, route.SpecialShopId),
                                new ShopMenuPathStep(ShopMenuPathStepKind.InclusionSubpage, route.Subpage, route.SpecialShopId),
                            ]).ToArray(),
                            requiredQuests.Concat(route.UnlockQuestId == 0 ? [] : [route.UnlockQuestId]).Distinct().ToArray(),
                            hasUnknownGate);
                    }
                    return;
                case ShopNpcEventKind.GilShop when validGilShopIds.Contains(current.RowId):
                    AddTerminal(ShopSheetKind.Gil, current.RowId, ResolveLinkKind(), path, requiredQuests, hasUnknownGate);
                    return;
                case ShopNpcEventKind.SpecialShop when validSpecialShopIds.Contains(current.RowId):
                    AddTerminal(ShopSheetKind.Special, current.RowId, ResolveLinkKind(), path, requiredQuests, hasUnknownGate);
                    return;
                case ShopNpcEventKind.GrandCompanyShop when validGrandCompanyShopIds.Contains(current.RowId):
                    AddTerminal(ShopSheetKind.GrandCompany, current.RowId, ShopNpcLinkKind.GrandCompanyShop, path, requiredQuests, hasUnknownGate);
                    return;
                case ShopNpcEventKind.FreeCompanyShop when validFreeCompanyShopIds.Contains(current.RowId):
                    AddTerminal(ShopSheetKind.FreeCompany, current.RowId, ShopNpcLinkKind.FreeCompanyShop, path, requiredQuests, hasUnknownGate);
                    return;
                default:
                    // CollectablesShop and DisposalShop are turn-in/sale surfaces;
                    // LotteryExchangeShop is nondeterministic. All fail closed here.
                    return;
            }

            ShopNpcLinkKind ResolveLinkKind()
                => viaCustomTalk
                    ? ShopNpcLinkKind.CustomTalk
                    : (viaTopicSelect, throughPreHandler) switch
                    {
                        (false, false) => ShopNpcLinkKind.DirectShop,
                        (true, false) => ShopNpcLinkKind.TopicSelectShop,
                        (false, true) => ShopNpcLinkKind.DirectPreHandler,
                        (true, true) => ShopNpcLinkKind.TopicSelectPreHandler,
                    };

            void AddTerminal(
                ShopSheetKind sheetKind,
                uint shopId,
                ShopNpcLinkKind linkKind,
                IReadOnlyList<ShopMenuPathStep> callbackPath,
                IReadOnlyList<uint> quests,
                bool unknownGate)
                => links.Add(new ShopNpcSheetLink(
                    sheetKind,
                    shopId,
                    npc.NpcId,
                    npc.NpcName,
                    callbackPath,
                    linkKind,
                    quests,
                    unknownGate));
        }
    }
}

internal static class ShopNpcPlacementBuilder
{
    private const float NearDuplicateDistance = 1f;

    public static IReadOnlyList<ShopNpcPlacementSheetRow> ExtractLgbPlacements(
        ShopTerritorySheetRow territory,
        string lgbPath,
        IReadOnlyList<ShopLgbInstanceObjectSheetRow> instances)
        => instances
            .Where(instance => instance.IsEventNpc && instance.BaseId != 0)
            .Select(instance => new ShopNpcPlacementSheetRow(
                instance.BaseId,
                territory.TerritoryId,
                territory.TerritoryName,
                instance.Position,
                0,
                ShopNpcPlacementSource.Lgb,
                territory.MapId,
                lgbPath))
            .ToArray();

    public static ShopNpcPlacementBuildResult Merge(
        IReadOnlyList<ShopNpcPlacementSheetRow> lgbPlacements,
        IReadOnlyList<ShopNpcPlacementSheetRow> levelPlacements,
        IReadOnlySet<uint> mappedTerritoryIds,
        IReadOnlySet<uint> linkedNpcIds,
        IReadOnlyList<ShopNpcPlacementSheetRow>? offlinePlacements = null)
    {
        offlinePlacements ??= [];
        var mappedLgb = lgbPlacements
            .Where(row => mappedTerritoryIds.Contains(row.TerritoryId))
            .ToArray();
        var unmappedLgbCount = lgbPlacements.Count - mappedLgb.Length;
        var linkedOffline = offlinePlacements
            .Where(row => linkedNpcIds.Contains(row.NpcId))
            .ToArray();
        var replacementNpcIds = linkedOffline
            .Where(row => row.ReplacesExisting)
            .Select(row => row.NpcId)
            .ToHashSet();
        var primary = mappedLgb
            .Concat(levelPlacements)
            .Where(row => !replacementNpcIds.Contains(row.NpcId))
            .ToArray();
        var primaryNpcIds = primary.Select(row => row.NpcId).ToHashSet();
        var fallbackOffline = linkedOffline
            .Where(row => row.ReplacesExisting || !primaryNpcIds.Contains(row.NpcId))
            .ToArray();
        var collected = primary.Concat(fallbackOffline).ToArray();
        var linked = collected.Where(row => linkedNpcIds.Contains(row.NpcId)).ToArray();
        var rawCollectedCount = mappedLgb.Length + levelPlacements.Count + offlinePlacements.Count;
        var unlinkedNpcCount = rawCollectedCount
            - mappedLgb.Count(row => linkedNpcIds.Contains(row.NpcId))
            - levelPlacements.Count(row => linkedNpcIds.Contains(row.NpcId))
            - linkedOffline.Length;
        var ordered = linked
            .OrderBy(row => row.Source)
            .ThenBy(row => row.NpcId)
            .ThenBy(row => row.TerritoryId)
            .ThenBy(row => row.SourcePath, StringComparer.Ordinal)
            .ThenBy(row => row.MapId)
            .ThenBy(row => row.LevelId)
            .ThenBy(row => row.Position.X)
            .ThenBy(row => row.Position.Y)
            .ThenBy(row => row.Position.Z)
            .ToArray();
        var retained = new List<ShopNpcPlacementSheetRow>();
        var nearDuplicateCount = 0;
        foreach (var candidate in ordered)
        {
            var isNearDuplicate = retained.Any(existing => AreNearDuplicate(existing, candidate));
            if (isNearDuplicate)
            {
                nearDuplicateCount++;
                continue;
            }

            retained.Add(candidate);
        }

        var placements = retained
            .OrderBy(row => row.NpcId)
            .ThenBy(row => row.TerritoryId)
            .ThenBy(row => row.Source)
            .ThenBy(row => row.MapId)
            .ThenBy(row => row.LevelId)
            .ThenBy(row => row.Position.X)
            .ThenBy(row => row.Position.Y)
            .ThenBy(row => row.Position.Z)
            .ThenBy(row => row.SourcePath, StringComparer.Ordinal)
            .ToArray();
        return new ShopNpcPlacementBuildResult(
            placements,
            mappedLgb.Length,
            levelPlacements.Count,
            unmappedLgbCount,
            unlinkedNpcCount,
            nearDuplicateCount,
            fallbackOffline.Length,
            replacementNpcIds.Count);
    }

    private static bool AreNearDuplicate(
        ShopNpcPlacementSheetRow left,
        ShopNpcPlacementSheetRow right)
    {
        if (left.NpcId != right.NpcId || left.TerritoryId != right.TerritoryId)
            return false;
        var thresholdSquared = NearDuplicateDistance * NearDuplicateDistance;
        if (left.RequiresFloorResolution || right.RequiresFloorResolution)
        {
            return Vector2.DistanceSquared(
                    new Vector2(left.Position.X, left.Position.Z),
                    new Vector2(right.Position.X, right.Position.Z))
                <= thresholdSquared;
        }
        return Vector3.DistanceSquared(left.Position, right.Position) <= thresholdSquared;
    }
}

internal static class ShopCatalogBuilder
{
    private sealed record OfferTemplate(
        ShopSheetKind SheetKind,
        ShopOfferKind Kind,
        uint ShopId,
        string ShopName,
        int RowIndex,
        uint ReceiveItemId,
        string ReceiveItemName,
        uint ReceiveCount,
        int TransactionsRequired,
        IReadOnlyList<ShopOfferOutput> Outputs,
        IReadOnlyList<ShopCurrencyCost> Currencies,
        IReadOnlyList<uint> RequiredQuestIds,
        bool HasUnknownGate,
        byte RequiredGrandCompany = 0,
        byte RequiredGrandCompanyRank = 0,
        byte RankTab = 0,
        byte CategoryTab = 0);

    public static ShopCatalogResolution Resolve(
        ShopCatalogSnapshot snapshot,
        uint itemId,
        int quantity,
        Action<string>? diagnostic = null)
    {
        diagnostic ??= _ => { };
        snapshot.Items.TryGetValue(itemId, out var requestedItem);
        var itemName = requestedItem?.Name ?? $"Item {itemId}";
        var templates = new List<OfferTemplate>();
        var unsupported = 0;
        var nonDivisible = 0;
        diagnostic($"Resolving item={itemId} ({itemName}), quantity={quantity}.");

        foreach (var row in snapshot.GilShopRows.Where(row => row.ItemId == itemId))
        {
            if (requestedItem == null || row.IsHq || requestedItem.PriceMid == 0)
            {
                unsupported++;
                diagnostic($"Rejected GilShop shop={row.ShopId}, row={row.RowIndex}: item missing, HQ, or zero gil price.");
                continue;
            }

            templates.Add(new OfferTemplate(
                ShopSheetKind.Gil,
                ShopOfferKind.GilShop,
                row.ShopId,
                row.ShopName,
                row.RowIndex,
                itemId,
                itemName,
                1,
                quantity,
                [new ShopOfferOutput(itemId, itemName, 1, requestedItem.StackSize, requestedItem.IsUnique)],
                [new ShopCurrencyCost(ShopCurrencyKind.Gil, 1, "Gil", requestedItem.PriceMid)],
                row.RequiredQuestIds,
                row.HasUnknownGate));
        }

        var tomestonesById = snapshot.Tomestones
            .GroupBy(row => row.TomestoneId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var row in snapshot.SpecialShopRows.Where(row => row.ReceiveItems.Any(item => item.ItemId == itemId)))
        {
            var receives = row.ReceiveItems.Where(item => item.ItemId != 0 && item.Count != 0).ToArray();
            var targets = receives.Where(item => item.ItemId == itemId).ToArray();
            if (targets.Length != 1
                || targets[0].IsHq
                || receives.Any(receive => receive.IsHq)
                || receives.Select(receive => receive.ItemId).Distinct().Count() != receives.Length
                || receives.Any(receive => !snapshot.Items.ContainsKey(receive.ItemId)))
            {
                unsupported++;
                diagnostic($"Rejected SpecialShop shop={row.ShopId}, row={row.RowIndex}: outputs are ambiguous, HQ, or missing item metadata.");
                continue;
            }

            var receiveCount = targets[0].Count;
            if (quantity % receiveCount != 0)
            {
                nonDivisible++;
                diagnostic($"Rejected SpecialShop shop={row.ShopId}, row={row.RowIndex}: bundle={receiveCount} cannot produce exact quantity={quantity}.");
                continue;
            }

            if (!TryNormalizeSpecialShopCosts(snapshot, tomestonesById, row, out var kind, out var currencies))
            {
                unsupported++;
                diagnostic($"Rejected SpecialShop shop={row.ShopId}, row={row.RowIndex}: unsupported or malformed cost family.");
                continue;
            }

            templates.Add(new OfferTemplate(
                ShopSheetKind.Special,
                kind,
                row.ShopId,
                row.ShopName,
                row.RowIndex,
                itemId,
                itemName,
                receiveCount,
                quantity / (int)receiveCount,
                receives.Select(receive =>
                {
                    var outputItem = snapshot.Items[receive.ItemId];
                    return new ShopOfferOutput(receive.ItemId, outputItem.Name, receive.Count, outputItem.StackSize, outputItem.IsUnique);
                }).ToArray(),
                currencies,
                row.RequiredQuestIds,
                row.HasUnknownGate));
        }

        foreach (var row in (snapshot.GrandCompanyShopRows ?? []).Where(row => row.ItemId == itemId))
        {
            if (requestedItem == null || row.SealCost == 0 || row.GrandCompanyId is < 1 or > 3)
            {
                unsupported++;
                continue;
            }
            var sealItemId = (uint)(19 + row.GrandCompanyId);
            templates.Add(new OfferTemplate(
                ShopSheetKind.GrandCompany,
                ShopOfferKind.GrandCompanyShop,
                row.ShopId,
                row.ShopName,
                row.RowIndex,
                itemId,
                itemName,
                1,
                quantity,
                [new ShopOfferOutput(itemId, itemName, 1, requestedItem.StackSize, requestedItem.IsUnique)],
                [new ShopCurrencyCost(ShopCurrencyKind.CompanySeal, sealItemId, "Company Seals", row.SealCost)],
                row.RequiredQuestIds,
                row.HasUnknownGate,
                row.GrandCompanyId,
                row.RequiredRank,
                row.RankTab,
                row.CategoryTab));
        }

        foreach (var row in (snapshot.FreeCompanyShopRows ?? []).Where(row => row.ItemId == itemId))
        {
            if (requestedItem == null || row.CreditCost == 0)
            {
                unsupported++;
                continue;
            }
            templates.Add(new OfferTemplate(
                ShopSheetKind.FreeCompany,
                ShopOfferKind.FreeCompanyShop,
                row.ShopId,
                row.ShopName,
                row.RowIndex,
                itemId,
                itemName,
                1,
                quantity,
                [new ShopOfferOutput(itemId, itemName, 1, requestedItem.StackSize, requestedItem.IsUnique)],
                [new ShopCurrencyCost(ShopCurrencyKind.FreeCompanyCredit, 0, "Free Company Credits", row.CreditCost)],
                row.RequiredQuestIds,
                true,
                RequiredGrandCompanyRank: row.RequiredRank));
        }

        var offers = new List<ShopOffer>();
        var unresolvedRoutes = 0;
        foreach (var template in templates)
        {
            var links = snapshot.NpcLinks
                .Where(link => link.ShopKind == template.SheetKind && link.ShopId == template.ShopId)
                .OrderBy(link => link.NpcId)
                .ThenBy(link => link.LinkKind)
                .ThenBy(link => CallbackPathSignature(link.CallbackPath), StringComparer.Ordinal)
                .ToArray();
            if (links.Length == 0)
            {
                unresolvedRoutes++;
                diagnostic($"Rejected shop={template.ShopId}, row={template.RowIndex}: no supported ENpc link path.");
                continue;
            }

            foreach (var link in links)
            {
                var placements = snapshot.NpcPlacements
                    .Where(placement => placement.NpcId == link.NpcId)
                    .OrderBy(placement => placement.TerritoryId)
                    .ThenBy(placement => placement.Source)
                    .ThenBy(placement => placement.MapId)
                    .ThenBy(placement => placement.LevelId)
                    .ThenBy(placement => placement.Position.X)
                    .ThenBy(placement => placement.Position.Y)
                    .ThenBy(placement => placement.Position.Z)
                    .ThenBy(placement => placement.SourcePath, StringComparer.Ordinal)
                    .ToArray();
                if (placements.Length == 0)
                {
                    unresolvedRoutes++;
                    diagnostic($"Rejected shop={template.ShopId}, npc={link.NpcId}, path={CallbackPathSignature(link.CallbackPath)}: no merged LGB/Level/offline placement.");
                    continue;
                }

                foreach (var placement in placements)
                {
                    var aetheryteRoutes = snapshot.Aetherytes
                        .Where(aetheryte => aetheryte.TerritoryId == placement.TerritoryId)
                        .Select(aetheryte => new ShopRouteCandidate(
                            aetheryte.AetheryteId,
                            aetheryte.Name,
                            aetheryte.Position,
                            Vector2.Distance(
                                new Vector2(aetheryte.Position.X, aetheryte.Position.Z),
                                new Vector2(placement.Position.X, placement.Position.Z))))
                        .OrderBy(route => route.DistanceToNpc)
                        .ThenBy(route => route.AetheryteId)
                        .ToArray();

                    var requiredQuests = template.RequiredQuestIds
                        .Concat(link.RequiredQuestIds)
                        .Where(questId => questId != 0)
                        .Distinct()
                        .OrderBy(questId => questId)
                        .ToArray();
                    var runtimeKind = link.LinkKind == ShopNpcLinkKind.InclusionShop
                        ? ShopOfferKind.InclusionShop
                        : template.Kind;
                    var offer = new ShopOffer(
                        runtimeKind,
                        template.ShopId,
                        template.ShopName,
                        template.RowIndex,
                        link.NpcId,
                        link.NpcName,
                        placement.TerritoryId,
                        placement.TerritoryName,
                        placement.Position,
                        link.CallbackPath,
                        link.LinkKind,
                        placement.Source,
                        placement.MapId,
                        placement.LevelId,
                        placement.SourcePath,
                        template.ReceiveItemId,
                        template.ReceiveItemName,
                        template.ReceiveCount,
                        template.TransactionsRequired,
                        template.Currencies,
                        requiredQuests,
                        template.HasUnknownGate || link.HasUnknownGate,
                        aetheryteRoutes,
                        placement.RequiresFloorResolution,
                        template.Outputs,
                        template.RequiredGrandCompany,
                        template.RequiredGrandCompanyRank,
                        template.RankTab,
                        template.CategoryTab);
                    offers.Add(offer);
                    diagnostic(
                        $"Candidate shop={offer.ShopId}, row={offer.SheetRowIndex}, npc={offer.NpcId}, territory={offer.TerritoryId}, "
                        + $"placement={offer.PlacementSource}, map={offer.PlacementMapId}, floorRequired={offer.RequiresFloorResolution}, path={CallbackPathSignature(offer.CallbackPath)}, "
                        + $"routes={offer.AetheryteRoutes.Count}, cost={CostDescription(offer.Currencies, offer.TransactionsRequired)}.");
                }
            }
        }

        diagnostic(
            $"Resolved item={itemId}: candidates={offers.Count}, unsupported={unsupported}, nonDivisible={nonDivisible}, unresolvedRoutes={unresolvedRoutes}.");

        return new ShopCatalogResolution(
            itemId,
            itemName,
            requestedItem?.StackSize ?? 0,
            requestedItem?.IsUnique ?? false,
            quantity,
            offers,
            unsupported,
            nonDivisible,
            unresolvedRoutes);
    }

    internal static string CallbackPathSignature(IReadOnlyList<ShopMenuPathStep> callbackPath)
        => callbackPath.Count == 0
            ? "direct-open"
            : string.Join('>', callbackPath.Select(step => $"{step.Kind}:{step.Index}:{step.HandlerId}"));

    private static string CostDescription(IReadOnlyList<ShopCurrencyCost> currencies, int transactions)
        => string.Join('+', currencies.Select(currency =>
            $"{currency.Kind}:{currency.ItemId}:{(long)currency.AmountPerTransaction * transactions}"));

    private static bool TryNormalizeSpecialShopCosts(
        ShopCatalogSnapshot snapshot,
        IReadOnlyDictionary<uint, ShopTomestoneSheetRow> tomestonesById,
        SpecialShopSheetRow row,
        out ShopOfferKind kind,
        out IReadOnlyList<ShopCurrencyCost> currencies)
    {
        kind = default;
        currencies = [];
        var nonzero = row.Costs.Where(cost => cost.Amount != 0 || cost.ItemOrCurrencyId != 0 || cost.Collectability != 0).ToArray();
        if (nonzero.Length == 0 || nonzero.Any(cost => cost.Amount == 0 || cost.ItemOrCurrencyId == 0 || cost.Collectability != 0))
            return false;

        if (nonzero.Length > 3)
            return false;

        var normalized = new List<ShopCurrencyCost>();
        foreach (var cost in nonzero)
        {
            if (!ShopCurrencyResolver.TryResolveSpecialShopCost(
                    snapshot.Items,
                    tomestonesById,
                    row.ShopId,
                    row.UseCurrencyType,
                    cost,
                    out var resolved))
                return false;
            normalized.Add(resolved);
        }

        try
        {
            currencies = normalized
                .GroupBy(currency => currency.Identity)
                .Select(group => new ShopCurrencyCost(
                    group.Key.Kind,
                    group.Key.ItemId,
                    group.First().Name,
                    checked((uint)group.Sum(currency => (long)currency.AmountPerTransaction))))
                .OrderBy(currency => currency.Kind)
                .ThenBy(currency => currency.ItemId)
                .ToArray();
        }
        catch (OverflowException)
        {
            return false;
        }

        kind = currencies.Select(currency => currency.Kind).Distinct().Count() > 1
            ? ShopOfferKind.SpecialShopMixed
            : currencies[0].Kind == ShopCurrencyKind.Tomestone
                ? ShopOfferKind.SpecialShopTomestone
                : ShopOfferKind.SpecialShopItem;
        return true;
    }
}

internal sealed record ShopSelectionContext(
    uint CurrentTerritoryId,
    Vector3 CurrentPosition,
    Func<uint, bool> IsAetheryteUnlocked,
    Func<uint, bool> IsQuestComplete,
    Func<ShopCurrencyCost, long> GetAvailableCurrency,
    Func<uint, long> GetItemCount,
    Func<uint, uint, long> GetInventoryCapacity,
    Func<byte>? GetGrandCompany = null,
    Func<byte>? GetGrandCompanyRank = null);

internal static class ShopOfferSelector
{
    public static ShopOfferSelectionResult Select(ShopCatalogResolution resolution, ShopSelectionContext context)
    {
        var evaluated = resolution.Offers
            .Select(offer => Evaluate(offer, resolution, context))
            .ToArray();

        if (resolution.Offers.Count == 0)
        {
            var detail = resolution.NonDivisibleOfferCount > 0
                ? "Available exchange bundles cannot produce the exact requested quantity."
                : resolution.UnsupportedOfferCount > 0
                    ? "Only unsupported shop offers were found for the requested item."
                    : resolution.UnresolvedRouteCount > 0
                        ? "Shop offers were found, but their NPC routes could not be resolved."
                        : "No supported shop offer was found for the requested item.";
            var code = resolution.UnresolvedRouteCount > 0
                && resolution.UnsupportedOfferCount == 0
                && resolution.NonDivisibleOfferCount == 0
                    ? ShopPurchaseFailureCodes.NoRoute
                    : ShopPurchaseFailureCodes.UnsupportedOffer;
            return new ShopOfferSelectionResult(null, evaluated, [], code, detail);
        }

        var gated = evaluated.Where(offer => offer.Availability != ShopAvailability.Denied).ToArray();
        var reachable = gated.Where(offer => offer.Route != null).ToArray();
        if (reachable.Length == 0)
        {
            return new ShopOfferSelectionResult(
                null,
                evaluated,
                [],
                ShopPurchaseFailureCodes.NoRoute,
                "No reachable, unlocked NPC route was available for a supported shop offer.");
        }

        var capacityAccepted = reachable.Where(offer => offer.CapacitySatisfied).ToArray();
        if (capacityAccepted.Length == 0)
        {
            return new ShopOfferSelectionResult(
                null,
                evaluated,
                [],
                ShopPurchaseFailureCodes.InventoryCapacity,
                $"Inventory cannot accept {resolution.RequestedQuantity} additional {resolution.ItemName}.");
        }

        var affordable = capacityAccepted.Where(offer => offer.Affordable).ToArray();
        if (affordable.Length == 0)
        {
            return new ShopOfferSelectionResult(
                null,
                evaluated,
                [],
                ShopPurchaseFailureCodes.InsufficientCurrency,
                "No reachable shop offer is affordable for the exact requested quantity.");
        }

        var knownAffordable = affordable.Where(offer => offer.BalanceKnown).ToArray();
        var candidatePool = knownAffordable.Length > 0 ? knownAffordable : affordable;
        var explicitlyAllowed = candidatePool.Where(offer => offer.Availability == ShopAvailability.Allowed).ToArray();
        if (explicitlyAllowed.Length > 0)
            candidatePool = explicitlyAllowed;

        var identitySets = candidatePool
            .Select(CurrencyIdentitySignature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identitySets.Length != 1)
        {
            return new ShopOfferSelectionResult(
                null,
                evaluated,
                [],
                ShopPurchaseFailureCodes.AmbiguousCurrency,
                "Affordable offers spend different currency identities; ADS will not choose what to spend.");
        }

        var minima = candidatePool
            .Where(candidate => !candidatePool.Any(other => !ReferenceEquals(other, candidate) && Dominates(other, candidate)))
            .ToArray();
        var distinctMinimumCosts = minima.Select(CostSignature).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctMinimumCosts.Length != 1)
        {
            return new ShopOfferSelectionResult(
                null,
                evaluated,
                [],
                ShopPurchaseFailureCodes.AmbiguousCurrency,
                "Affordable offers have incomparable costs; ADS will not choose what to spend.");
        }

        var ordered = OrderCandidates(minima, context).ToArray();
        var selected = ordered[0];
        var fallbacks = OrderCandidates(
                candidatePool.Where(offer => !ReferenceEquals(offer, selected) && EqualCosts(offer, selected)),
                context)
            .ToArray();
        return new ShopOfferSelectionResult(selected, evaluated, fallbacks, null, "Supported shop offer selected.");
    }

    public static ShopPurchaseOfferStatus ToStatus(EvaluatedShopOffer evaluated)
        => new(
            KindName(evaluated.Offer.Kind),
            evaluated.Offer.ShopId,
            evaluated.Offer.ShopName,
            evaluated.Offer.SheetRowIndex,
            evaluated.Offer.NpcId,
            evaluated.Offer.NpcName,
            evaluated.Offer.TerritoryId,
            evaluated.Offer.TerritoryName,
            evaluated.Offer.ReceiveCount,
            evaluated.Currencies.Select(currency => new ShopPurchaseCurrencyStatus(
                CurrencyKindName(currency.Currency.Kind),
                currency.Currency.ItemId,
                currency.Currency.Name,
                currency.RequiredAmount,
                currency.AvailableAmount,
                currency.AvailabilityKnown)).ToArray(),
            evaluated.Offer.AllOutputs.Select(output => new ShopPurchaseOutputStatus(
                output.ItemId,
                output.Name,
                output.Count)).ToArray(),
            evaluated.Availability == ShopAvailability.Allowed && evaluated.BalanceKnown);

    private static EvaluatedShopOffer Evaluate(
        ShopOffer offer,
        ShopCatalogResolution resolution,
        ShopSelectionContext context)
    {
        var availability = offer.RequiredQuestIds.Any(questId => !context.IsQuestComplete(questId))
            ? ShopAvailability.Denied
            : offer.HasUnknownGate
                ? ShopAvailability.Deferred
                : ShopAvailability.Allowed;
        if (offer.RequiredGrandCompany != 0)
        {
            var currentCompany = context.GetGrandCompany?.Invoke() ?? 0;
            var currentRank = context.GetGrandCompanyRank?.Invoke() ?? 0;
            availability = currentCompany == 0 || currentRank == 0
                ? ShopAvailability.Deferred
                : currentCompany != offer.RequiredGrandCompany || currentRank < offer.RequiredGrandCompanyRank
                    ? ShopAvailability.Denied
                    : availability;
        }
        var gateSatisfied = availability != ShopAvailability.Denied;
        var targetCapacity = resolution.StackSize == 0
            ? 0
            : context.GetInventoryCapacity(resolution.ItemId, resolution.StackSize);
        var capacitySatisfied = targetCapacity >= resolution.RequestedQuantity
            && (!resolution.IsUnique
                || (resolution.RequestedQuantity == 1 && context.GetItemCount(resolution.ItemId) == 0));
        foreach (var output in offer.AllOutputs)
        {
            long total;
            try
            {
                total = checked((long)output.Count * offer.TransactionsRequired);
            }
            catch (OverflowException)
            {
                capacitySatisfied = false;
                break;
            }

            var capacity = context.GetInventoryCapacity(output.ItemId, Math.Max(1, output.StackSize));
            if (capacity < total || (output.IsUnique && (total != 1 || context.GetItemCount(output.ItemId) != 0)))
            {
                capacitySatisfied = false;
                break;
            }
        }
        ResolvedShopRoute? route = null;
        if (offer.TerritoryId == context.CurrentTerritoryId)
        {
            route = new ResolvedShopRoute(
                offer.TerritoryId,
                offer.TerritoryName,
                offer.NpcPosition,
                false,
                0,
                string.Empty,
                Vector3.Distance(context.CurrentPosition, offer.NpcPosition),
                offer.RequiresFloorResolution);
        }
        else
        {
            var aetheryte = offer.AetheryteRoutes
                .Where(candidate => context.IsAetheryteUnlocked(candidate.AetheryteId))
                .OrderBy(candidate => candidate.DistanceToNpc)
                .ThenBy(candidate => candidate.AetheryteId)
                .FirstOrDefault();
            if (aetheryte != null)
            {
                route = new ResolvedShopRoute(
                    offer.TerritoryId,
                    offer.TerritoryName,
                    offer.NpcPosition,
                    true,
                    aetheryte.AetheryteId,
                    aetheryte.AetheryteName,
                    aetheryte.DistanceToNpc,
                    offer.RequiresFloorResolution);
            }
        }

        var currencies = new List<EvaluatedShopCurrency>();
        var affordable = true;
        var balanceKnown = true;
        foreach (var currency in offer.Currencies)
        {
            long required;
            try
            {
                required = checked((long)currency.AmountPerTransaction * offer.TransactionsRequired);
            }
            catch (OverflowException)
            {
                required = long.MaxValue;
            }

            var available = context.GetAvailableCurrency(currency);
            var known = available >= 0;
            balanceKnown &= known;
            currencies.Add(new EvaluatedShopCurrency(currency, required, known ? available : -1, known));
            affordable &= !known || required <= available;
        }

        var failure = !gateSatisfied
            ? ShopPurchaseFailureCodes.NoRoute
            : route == null
                ? ShopPurchaseFailureCodes.NoRoute
                : !capacitySatisfied
                    ? ShopPurchaseFailureCodes.InventoryCapacity
                    : !affordable
                        ? ShopPurchaseFailureCodes.InsufficientCurrency
                        : null;
        return new EvaluatedShopOffer(
            offer,
            route,
            currencies,
            gateSatisfied,
            affordable,
            capacitySatisfied,
            failure,
            availability,
            balanceKnown);
    }

    private static IOrderedEnumerable<EvaluatedShopOffer> OrderCandidates(
        IEnumerable<EvaluatedShopOffer> candidates,
        ShopSelectionContext context)
        => candidates
            .OrderBy(offer => offer.Offer.TerritoryId == context.CurrentTerritoryId ? 0 : 1)
            .ThenBy(offer => offer.Route!.RequiresTeleport ? 1 : 0)
            .ThenBy(offer => offer.Route!.RouteDistance)
            .ThenBy(offer => offer.Offer.ShopId)
            .ThenBy(offer => offer.Offer.NpcId)
            .ThenBy(offer => offer.Offer.SheetRowIndex)
            .ThenBy(offer => offer.Offer.PlacementSource)
            .ThenBy(offer => offer.Offer.PlacementMapId)
            .ThenBy(offer => offer.Offer.PlacementLevelId)
            .ThenBy(offer => offer.Offer.NpcPosition.X)
            .ThenBy(offer => offer.Offer.NpcPosition.Y)
            .ThenBy(offer => offer.Offer.NpcPosition.Z)
            .ThenBy(offer => ShopCatalogBuilder.CallbackPathSignature(offer.Offer.CallbackPath), StringComparer.Ordinal)
            .ThenBy(offer => offer.Route!.AetheryteId);

    private static bool Dominates(EvaluatedShopOffer left, EvaluatedShopOffer right)
    {
        var leftCosts = left.Currencies.ToDictionary(currency => currency.Currency.Identity, currency => currency.RequiredAmount);
        var rightCosts = right.Currencies.ToDictionary(currency => currency.Currency.Identity, currency => currency.RequiredAmount);
        return leftCosts.Keys.SetEquals(rightCosts.Keys)
            && leftCosts.All(pair => pair.Value <= rightCosts[pair.Key])
            && leftCosts.Any(pair => pair.Value < rightCosts[pair.Key]);
    }

    private static bool EqualCosts(EvaluatedShopOffer left, EvaluatedShopOffer right)
        => CostSignature(left) == CostSignature(right)
            && OutputSignature(left) == OutputSignature(right);

    private static string OutputSignature(EvaluatedShopOffer offer)
        => string.Join('|', offer.Offer.AllOutputs
            .OrderBy(output => output.ItemId)
            .Select(output => $"{output.ItemId}:{output.Count}"));

    private static string CurrencyIdentitySignature(EvaluatedShopOffer offer)
        => string.Join('|', offer.Currencies
            .OrderBy(currency => currency.Currency.Kind)
            .ThenBy(currency => currency.Currency.ItemId)
            .Select(currency => $"{(int)currency.Currency.Kind}:{currency.Currency.ItemId}"));

    private static string CostSignature(EvaluatedShopOffer offer)
        => string.Join('|', offer.Currencies
            .OrderBy(currency => currency.Currency.Kind)
            .ThenBy(currency => currency.Currency.ItemId)
            .Select(currency => $"{(int)currency.Currency.Kind}:{currency.Currency.ItemId}:{currency.RequiredAmount}"));

    private static string KindName(ShopOfferKind kind)
        => kind switch
        {
            ShopOfferKind.GilShop => "gil-shop",
            ShopOfferKind.SpecialShopItem => "special-shop-item",
            ShopOfferKind.SpecialShopTomestone => "special-shop-tomestone",
            ShopOfferKind.SpecialShopMixed => "special-shop-mixed",
            ShopOfferKind.InclusionShop => "inclusion-shop",
            ShopOfferKind.GrandCompanyShop => "grand-company-shop",
            ShopOfferKind.FreeCompanyShop => "free-company-shop",
            _ => "unsupported",
        };

    private static string CurrencyKindName(ShopCurrencyKind kind)
        => kind switch
        {
            ShopCurrencyKind.Gil => "gil",
            ShopCurrencyKind.Item => "item",
            ShopCurrencyKind.Tomestone => "tomestone",
            ShopCurrencyKind.CompanySeal => "company-seal",
            ShopCurrencyKind.Mgp => "mgp",
            ShopCurrencyKind.WolfMark => "wolf-mark",
            ShopCurrencyKind.AlliedSeal => "allied-seal",
            ShopCurrencyKind.CurrencyManager => "currency-manager",
            ShopCurrencyKind.FreeCompanyCredit => "free-company-credit",
            _ => "unknown",
        };
}

internal sealed class LuminaShopSheetSource(IDataManager dataManager, IPluginLog log) : IShopSheetSource
{
    public ShopCatalogSnapshot BuildSnapshot()
    {
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var items = itemSheet.ToDictionary(
            item => item.RowId,
            item => new ShopItemSheetRow(
                item.RowId,
                item.Name.ToString(),
                item.StackSize,
                item.PriceMid,
                item.IsUnique));
        var gilShops = dataManager.GetExcelSheet<GilShop>().ToDictionary(row => row.RowId);
        var gilRows = new List<GilShopSheetRow>();
        foreach (var subrow in dataManager.GetSubrowExcelSheet<GilShopItem>().Flatten())
        {
            gilShops.TryGetValue(subrow.RowId, out var shop);
            var quests = subrow.QuestRequired.Select(row => row.RowId)
                .Append(shop.Quest.RowId)
                .Where(id => id != 0)
                .Distinct()
                .ToArray();
            gilRows.Add(new GilShopSheetRow(
                subrow.RowId,
                string.IsNullOrWhiteSpace(shop.Name.ToString()) ? $"Gil Shop {subrow.RowId}" : shop.Name.ToString(),
                subrow.SubrowId,
                subrow.Item.RowId,
                subrow.IsHQ,
                quests,
                subrow.AchievementRequired.RowId != 0
                    || (subrow.StateRequired != 0 && quests.Length != 0)
                    || shop.FestivalId != 0));
        }

        var specialRows = new List<SpecialShopSheetRow>();
        foreach (var shop in dataManager.GetExcelSheet<SpecialShop>())
        {
            for (var rowIndex = 0; rowIndex < shop.Item.Count; rowIndex++)
            {
                var row = shop.Item[rowIndex];
                var receives = row.ReceiveItems
                    .Select(receive => (receive.Item.RowId, receive.ReceiveCount, receive.ReceiveHq))
                    .Where(receive => receive.RowId != 0 || receive.ReceiveCount != 0)
                    .Select(receive => (ItemId: receive.RowId, Count: receive.ReceiveCount, IsHq: receive.ReceiveHq))
                    .ToArray();
                if (receives.Length == 0)
                    continue;

                var costs = row.ItemCosts
                    .Select(cost => new SpecialShopCostSheetRow(
                        cost.ItemCost.RowId,
                        cost.CurrencyCost,
                        cost.CollectabilityCost,
                        cost.CostType))
                    .ToArray();
                var quests = new[] { row.Quest.RowId, shop.Quest.RowId }
                    .Where(id => id != 0)
                    .Distinct()
                    .ToArray();
                var unknownGate = row.AchievementUnlock.RowId != 0
                    || shop.RequiredContentFinderCondition.RowId != 0
                    || shop.RequiredFestival.RowId != 0;
                specialRows.Add(new SpecialShopSheetRow(
                    shop.RowId,
                    string.IsNullOrWhiteSpace(shop.Name.ToString()) ? $"Special Shop {shop.RowId}" : shop.Name.ToString(),
                    rowIndex,
                    receives,
                    costs,
                    shop.UseCurrencyType,
                    quests,
                    unknownGate));
            }
        }

        var tomestones = dataManager.GetExcelSheet<TomestonesItem>()
            .Where(row => row.Item.RowId != 0)
            .Select(row => new ShopTomestoneSheetRow(
                row.Tomestones.RowId,
                row.Item.RowId,
                items.TryGetValue(row.Item.RowId, out var item) ? item.Name : $"Item {row.Item.RowId}"))
            .ToArray();
        var gcShopsByCompany = dataManager.GetExcelSheet<GCShop>()
            .Where(row => row.GrandCompany.RowId != 0)
            .GroupBy(row => row.GrandCompany.RowId)
            .ToDictionary(group => group.Key, group => group.First().RowId);
        var gcCategories = dataManager.GetExcelSheet<GCScripShopCategory>().ToDictionary(row => row.RowId);
        var grandCompanyRows = new List<GrandCompanyShopSheetRow>();
        foreach (var row in dataManager.GetSubrowExcelSheet<GCScripShopItem>().Flatten())
        {
            if (!gcCategories.TryGetValue(row.RowId, out var category)
                || !gcShopsByCompany.TryGetValue(category.GrandCompany.RowId, out var shopId)
                || row.Item.RowId == 0
                || row.CostGCSeals == 0)
                continue;
            var requiredRank = checked((byte)row.RequiredGrandCompanyRank.RowId);
            var rankTab = requiredRank <= 4 ? (byte)0 : requiredRank <= 8 ? (byte)1 : (byte)2;
            var categoryTab = category.SubCategory switch
            {
                1 => (byte)2,
                2 => (byte)0,
                3 => (byte)1,
                4 => (byte)3,
                _ => byte.MaxValue,
            };
            if (categoryTab == byte.MaxValue)
                continue;
            grandCompanyRows.Add(new GrandCompanyShopSheetRow(
                shopId,
                $"Grand Company Shop {category.GrandCompany.RowId}",
                row.SubrowId,
                row.Item.RowId,
                row.CostGCSeals,
                checked((byte)category.GrandCompany.RowId),
                requiredRank,
                rankTab,
                categoryTab,
                [],
                false));
        }

        var freeCompanyRows = new List<FreeCompanyShopSheetRow>();
        foreach (var shop in dataManager.GetExcelSheet<FccShop>())
        {
            for (var index = 0; index < shop.ItemData.Count; index++)
            {
                var row = shop.ItemData[index];
                if (row.Item.RowId == 0 || row.Cost == 0)
                    continue;
                freeCompanyRows.Add(new FreeCompanyShopSheetRow(
                    shop.RowId,
                    string.IsNullOrWhiteSpace(shop.Name.ToString()) ? $"Free Company Shop {shop.RowId}" : shop.Name.ToString(),
                    index,
                    row.Item.RowId,
                    row.Cost,
                    checked((byte)row.FCRankRequired.RowId),
                    [],
                    false));
            }
        }

        var links = BuildNpcLinks(gilRows, specialRows, grandCompanyRows, freeCompanyRows);
        var aetherytes = BuildAetherytes();
        var placements = BuildNpcPlacements(links, aetherytes.Routes);

        log.Information(
            "[ADS][Shop] Catalog cached: items={Items}, gilRows={GilRows}, specialRows={SpecialRows}, links={Links}, placements={Placements}, lgbCollected={LgbCollected}, levelCollected={LevelCollected}, offlineFallback={OfflineFallback}, replacements={Replacements}, nearDuplicates={NearDuplicates}, unlinkedPlacements={UnlinkedPlacements}, aetherytesMapped={AetherytesMapped}, aetherytesRejected={AetherytesRejected}, aetherytesMissing={AetherytesMissing}.",
            items.Count,
            gilRows.Count,
            specialRows.Count,
            links.Count,
            placements.Placements.Count,
            placements.LgbCollectedCount,
            placements.LevelCollectedCount,
            placements.OfflineCatalogCount,
            placements.ReplacementCount,
            placements.NearDuplicateCount,
            placements.UnlinkedNpcCount,
            aetherytes.MappedCount,
            aetherytes.RejectedCount,
            aetherytes.MissingCount);
        return new ShopCatalogSnapshot(
            items,
            gilRows,
            specialRows,
            links,
            placements.Placements,
            aetherytes.Routes,
            tomestones,
            grandCompanyRows,
            freeCompanyRows);
    }

    private List<ShopNpcSheetLink> BuildNpcLinks(
        IReadOnlyCollection<GilShopSheetRow> gilRows,
        IReadOnlyCollection<SpecialShopSheetRow> specialRows,
        IReadOnlyCollection<GrandCompanyShopSheetRow> grandCompanyRows,
        IReadOnlyCollection<FreeCompanyShopSheetRow> freeCompanyRows)
    {
        var validGilIds = gilRows.Select(row => row.ShopId).ToHashSet();
        var validSpecialIds = specialRows.Select(row => row.ShopId).ToHashSet();
        var validGrandCompanyIds = grandCompanyRows.Select(row => row.ShopId).ToHashSet();
        var validFreeCompanyIds = freeCompanyRows.Select(row => row.ShopId).ToHashSet();
        var residentSheet = dataManager.GetExcelSheet<ENpcResident>();
        var npcs = dataManager.GetExcelSheet<ENpcBase>()
            .Select(npc =>
            {
                var npcName = residentSheet.GetRowOrDefault(npc.RowId)?.Singular.ToString();
                if (string.IsNullOrWhiteSpace(npcName))
                    npcName = $"ENpc {npc.RowId}";
                return new ShopNpcEventSheetRow(
                    npc.RowId,
                    npcName,
                    npc.ENpcData.Select(ToEventReference).ToArray());
            })
            .ToArray();
        var topics = dataManager.GetExcelSheet<TopicSelect>()
            .Select(topic => new ShopTopicSelectSheetRow(
                topic.RowId,
                topic.Shop.Select(ToEventReference).ToArray()))
            .ToArray();
        var preHandlers = dataManager.GetExcelSheet<PreHandler>()
            .Select(preHandler => new ShopPreHandlerSheetRow(
                preHandler.RowId,
                ToEventReference(preHandler.Target),
                preHandler.UnlockQuest.RowId,
                false))
            .ToArray();
        var fateShops = dataManager.GetExcelSheet<FateShop>()
            .Select(shop => new ShopFateShopSheetRow(
                shop.RowId,
                shop.SpecialShop.Select(row => row.RowId).Where(rowId => rowId != 0).ToArray()))
            .ToArray();
        var inclusionSeries = dataManager.GetSubrowExcelSheet<InclusionShopSeries>()
            .Flatten()
            .GroupBy(row => row.RowId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SubrowId).ToArray());
        var inclusionRoutes = new List<ShopInclusionRouteSheetRow>();
        foreach (var shop in dataManager.GetExcelSheet<InclusionShop>())
        {
            for (var page = 0; page < shop.Category.Count; page++)
            {
                var category = shop.Category[page];
                if (category.RowId == 0
                    || !inclusionSeries.TryGetValue(category.Value.InclusionShopSeries.RowId, out var series))
                    continue;
                for (var subpage = 0; subpage < series.Length; subpage++)
                {
                    if (series[subpage].SpecialShop.RowId == 0)
                        continue;
                    inclusionRoutes.Add(new ShopInclusionRouteSheetRow(
                        shop.RowId,
                        series[subpage].SpecialShop.RowId,
                        page,
                        subpage,
                        shop.UnlockQuest.RowId));
                }
            }
        }

        var customTalkRows = dataManager.GetExcelSheet<CustomTalk>().ToArray();
        var customTalkNestHandlers = dataManager.GetSubrowExcelSheet<CustomTalkNestHandlers>()
            .Flatten()
            .GroupBy(row => row.RowId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => ToEventReference(row.NestHandler))
                    .Where(reference => reference.Kind != ShopNpcEventKind.Unsupported)
                    .ToArray());
        var knownReferences = new (ShopNpcEventKind Kind, IReadOnlySet<uint> Ids)[]
        {
            (ShopNpcEventKind.GilShop, validGilIds),
            (ShopNpcEventKind.SpecialShop, validSpecialIds),
            (ShopNpcEventKind.TopicSelect, topics.Select(row => row.TopicSelectId).ToHashSet()),
            (ShopNpcEventKind.PreHandler, preHandlers.Select(row => row.PreHandlerId).ToHashSet()),
            (ShopNpcEventKind.FateShop, fateShops.Select(row => row.FateShopId).ToHashSet()),
            (ShopNpcEventKind.InclusionShop, inclusionRoutes.Select(row => row.InclusionShopId).ToHashSet()),
            (ShopNpcEventKind.GrandCompanyShop, validGrandCompanyIds),
            (ShopNpcEventKind.FreeCompanyShop, validFreeCompanyIds),
            (ShopNpcEventKind.CustomTalk, customTalkRows.Select(row => row.RowId).ToHashSet()),
        };
        var customTalks = customTalkRows.Select(row =>
        {
            var references = new List<ShopNpcEventReference>();
            if (row.SpecialLinks.IsSubrow<CustomTalkNestHandlers>()
                && customTalkNestHandlers.TryGetValue(row.SpecialLinks.RowId, out var nested))
                references.AddRange(nested);
            else
            {
                var specialLink = ToEventReference(row.SpecialLinks);
                if (specialLink.Kind != ShopNpcEventKind.Unsupported)
                    references.Add(specialLink);
            }
            foreach (var script in row.Script.Where(script => script.ScriptArg != 0))
            {
                var matches = knownReferences.Where(candidate => candidate.Ids.Contains(script.ScriptArg)).ToArray();
                if (matches.Length == 1)
                    references.Add(new ShopNpcEventReference(matches[0].Kind, script.ScriptArg));
                else if (matches.Length == 0 && customTalkNestHandlers.TryGetValue(script.ScriptArg, out var scriptNested))
                    references.AddRange(scriptNested);
            }
            return new ShopCustomTalkSheetRow(row.RowId, references.Distinct().ToArray());
        }).ToArray();

        var links = ShopNpcLinkBuilder.Build(
            validGilIds,
            validSpecialIds,
            npcs,
            topics,
            preHandlers,
            validGrandCompanyIds,
            validFreeCompanyIds,
            fateShops,
            inclusionRoutes,
            customTalks).ToList();
        foreach (var group in links.GroupBy(link => link.LinkKind).OrderBy(group => group.Key))
            log.Debug("[ADS][Shop] Link catalog kind={LinkKind}, count={Count}.", group.Key, group.Count());
        return links;
    }

    private ShopNpcPlacementBuildResult BuildNpcPlacements(
        IReadOnlyCollection<ShopNpcSheetLink> links,
        IReadOnlyCollection<ShopAetheryteSheetRow> aetherytes)
    {
        var linkedNpcIds = links.Select(link => link.NpcId).ToHashSet();
        var mappedTerritoryIds = aetherytes.Select(route => route.TerritoryId).ToHashSet();
        var territories = dataManager.GetExcelSheet<TerritoryType>()
            .Where(territory => mappedTerritoryIds.Contains(territory.RowId))
            .Select(territory => new ShopTerritorySheetRow(
                territory.RowId,
                GameInteractionHelper.GetTerritoryName(dataManager, territory.RowId),
                territory.Map.RowId,
                territory.Bg.ToString()))
            .OrderBy(territory => territory.TerritoryId)
            .ToArray();
        var lgbPlacements = new List<ShopNpcPlacementSheetRow>();
        var derivedFiles = 0;
        var loadedFiles = 0;
        var missingFiles = 0;
        var malformedPaths = 0;
        var failedFiles = 0;
        foreach (var territory in territories)
        {
            if (!ShopLgbPathBuilder.TryDerivePlaneventPath(territory.BackgroundPath, out var lgbPath))
            {
                malformedPaths++;
                log.Debug(
                    "[ADS][Shop] LGB path rejected territory={TerritoryId}, bg={BackgroundPath}.",
                    territory.TerritoryId,
                    territory.BackgroundPath);
                continue;
            }

            derivedFiles++;
            try
            {
                var lgb = dataManager.GetFile<LgbFile>(lgbPath);
                if (lgb == null)
                {
                    missingFiles++;
                    log.Debug(
                        "[ADS][Shop] LGB file missing territory={TerritoryId}, file={LgbPath}.",
                        territory.TerritoryId,
                        lgbPath);
                    continue;
                }

                loadedFiles++;
                var instances = new List<ShopLgbInstanceObjectSheetRow>();
                foreach (var layer in lgb.Layers)
                {
                    foreach (var instance in layer.InstanceObjects)
                    {
                        if (instance.AssetType == LayerEntryType.EventNPC
                            && instance.Object is LayerCommon.ENPCInstanceObject eventNpc)
                        {
                            instances.Add(new ShopLgbInstanceObjectSheetRow(
                                true,
                                eventNpc.ParentData.ParentData.BaseId,
                                new Vector3(
                                    instance.Transform.Translation.X,
                                    instance.Transform.Translation.Y,
                                    instance.Transform.Translation.Z)));
                        }
                        else
                        {
                            instances.Add(new ShopLgbInstanceObjectSheetRow(false, 0, default));
                        }
                    }
                }

                var extracted = ShopNpcPlacementBuilder.ExtractLgbPlacements(territory, lgbPath, instances);
                lgbPlacements.AddRange(extracted);
                log.Debug(
                    "[ADS][Shop] LGB file loaded territory={TerritoryId}, file={LgbPath}, eventNpcPlacements={Placements}.",
                    territory.TerritoryId,
                    lgbPath,
                    extracted.Count);
            }
            catch (Exception ex)
            {
                failedFiles++;
                log.Warning(
                    ex,
                    "[ADS][Shop] LGB file failed territory={TerritoryId}, file={LgbPath}.",
                    territory.TerritoryId,
                    lgbPath);
            }
        }

        var levelPlacements = new List<ShopNpcPlacementSheetRow>();
        foreach (var level in dataManager.GetExcelSheet<Level>())
        {
            if (level.Type != 8 || !level.Object.Is<ENpcBase>())
                continue;

            var territoryId = level.Territory.RowId;
            levelPlacements.Add(new ShopNpcPlacementSheetRow(
                level.Object.RowId,
                territoryId,
                GameInteractionHelper.GetTerritoryName(dataManager, territoryId),
                new Vector3(level.X, level.Y, level.Z),
                level.RowId,
                ShopNpcPlacementSource.Level,
                level.Map.RowId,
                "Level"));
        }

        IReadOnlyList<ShopNpcPlacementSheetRow> offlinePlacements;
        try
        {
            offlinePlacements = ShopOfflineNpcPlacementCatalog.LoadEmbedded()
                .Select(row => row.ToSheetRow())
                .ToArray();
            log.Debug(
                "[ADS][Shop] Offline placement resource loaded rows={Rows}, linkedRows={LinkedRows}, replacements={Replacements}.",
                offlinePlacements.Count,
                offlinePlacements.Count(row => linkedNpcIds.Contains(row.NpcId)),
                offlinePlacements.Count(row => row.ReplacesExisting));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Shop] Offline placement resource failed to load; LGB and Level sources remain available.");
            offlinePlacements = [];
        }

        var result = ShopNpcPlacementBuilder.Merge(
            lgbPlacements,
            levelPlacements,
            mappedTerritoryIds,
            linkedNpcIds,
            offlinePlacements);
        log.Information(
            "[ADS][Shop] Placement catalog: mappedTerritories={MappedTerritories}, territoryRows={TerritoryRows}, lgbDerived={LgbDerived}, lgbLoaded={LgbLoaded}, lgbMissing={LgbMissing}, lgbMalformed={LgbMalformed}, lgbFailed={LgbFailed}, lgbPlacements={LgbPlacements}, levelPlacements={LevelPlacements}, offlineFallback={OfflineFallback}, replacements={Replacements}, unlinked={Unlinked}, deduplicated={Deduplicated}, retained={Retained}.",
            mappedTerritoryIds.Count,
            territories.Length,
            derivedFiles,
            loadedFiles,
            missingFiles,
            malformedPaths,
            failedFiles,
            result.LgbCollectedCount,
            result.LevelCollectedCount,
            result.OfflineCatalogCount,
            result.ReplacementCount,
            result.UnlinkedNpcCount,
            result.NearDuplicateCount,
            result.Placements.Count);
        return result;
    }

    private static ShopNpcEventReference ToEventReference(Lumina.Excel.RowRef row)
        => row.Is<GilShop>()
            ? new ShopNpcEventReference(ShopNpcEventKind.GilShop, row.RowId)
            : row.Is<SpecialShop>()
                ? new ShopNpcEventReference(ShopNpcEventKind.SpecialShop, row.RowId)
                : row.Is<TopicSelect>()
                    ? new ShopNpcEventReference(ShopNpcEventKind.TopicSelect, row.RowId)
                : row.Is<PreHandler>()
                    ? new ShopNpcEventReference(ShopNpcEventKind.PreHandler, row.RowId)
                    : row.Is<FateShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.FateShop, row.RowId)
                    : row.Is<InclusionShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.InclusionShop, row.RowId)
                    : row.Is<GCShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.GrandCompanyShop, row.RowId)
                    : row.Is<FccShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.FreeCompanyShop, row.RowId)
                    : row.Is<CustomTalk>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.CustomTalk, row.RowId)
                    : row.Is<CollectablesShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.CollectablesShop, row.RowId)
                    : row.Is<DisposalShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.DisposalShop, row.RowId)
                    : row.Is<LotteryExchangeShop>()
                        ? new ShopNpcEventReference(ShopNpcEventKind.LotteryExchangeShop, row.RowId)
                        : new ShopNpcEventReference(ShopNpcEventKind.Unsupported, row.RowId);

    private ShopAetheryteBuildResult BuildAetherytes()
    {
        var aetherytes = dataManager.GetExcelSheet<Aetheryte>()
            .Where(row => row.IsAetheryte)
            .Select(row => new ShopAetheryteSourceRow(
                row.RowId,
                row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty,
                row.Territory.RowId,
                row.Map.RowId,
                row.IsAetheryte))
            .ToArray();
        var maps = dataManager.GetExcelSheet<Map>()
            .Select(row => new ShopMapSheetRow(
                row.RowId,
                row.TerritoryType.RowId,
                row.MapMarkerRange,
                row.SizeFactor,
                row.OffsetX,
                row.OffsetY))
            .ToArray();
        var markers = dataManager.GetSubrowExcelSheet<MapMarker>()
            .Flatten()
            .Where(row => row.DataType == 3)
            .Select(row => new ShopMapMarkerSheetRow(
                row.RowId,
                row.SubrowId,
                row.DataType,
                row.DataKey.RowId,
                row.X,
                row.Y))
            .ToArray();

        return ShopAetheryteRouteBuilder.Build(aetherytes, maps, markers);
    }
}

internal static class DictionaryKeySetExtensions
{
    public static bool SetEquals<T>(this IEnumerable<T> left, IEnumerable<T> right)
        => left.ToHashSet().SetEquals(right);
}
