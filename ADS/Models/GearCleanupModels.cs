using FFXIVClientStructs.FFXIV.Client.Game;

namespace ADS.Models;

public enum GearCleanupScope { Armoury, Inventory, Both }

public sealed record GearCleanupCandidate(
    InventoryType Container, ushort Slot, InventoryItem Item, string Name,
    bool WhiteEquipment, bool NpcBuyable);

public sealed record GearCleanupSelection(
    ulong CharacterId, GearCleanupScope Scope, IReadOnlyList<GearCleanupCandidate> Items, bool AllUnprotected = false);

public sealed record GearSaleStatusSnapshot(string? OperationId, string Scope, bool Running, bool Done,
    bool? Succeeded, int RequestedCount, int SoldCount, long GilEarned, string StatusMessage,
    bool CommandSent = false, bool Settled = true, string LastStartError = "", int SkippedCount = 0);

internal enum GearSaleState { Ready, Pending, Unsupported, NotReady, Confirmation, Unavailable }

internal static class GearCleanupPolicy
{
    internal static readonly InventoryType[] Bags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    internal static readonly InventoryType[] Armoury =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    internal static IEnumerable<InventoryType> Containers(GearCleanupScope scope, bool allUnprotected = false)
    {
        var armoury = allUnprotected ? Armoury.Append(InventoryType.ArmorySoulCrystal) : Armoury.AsEnumerable();
        return scope switch { GearCleanupScope.Armoury => armoury, GearCleanupScope.Inventory => Bags, _ => armoury.Concat(Bags) };
    }

    internal static bool IsEligible(GearCleanupCandidate candidate, GearCleanupScope scope, bool selling, IReadOnlySet<uint> protectedItems,
        bool allUnprotected = false)
        => Containers(scope, !selling && allUnprotected).Contains(candidate.Container) && (candidate.WhiteEquipment || (!selling && allUnprotected))
            && !candidate.Item.IsSymbolic && candidate.Item.ItemId != 0 && candidate.Item.Quantity == 1
            && (!selling || candidate.NpcBuyable)
            && !protectedItems.Contains(ADS.Services.DesynthPolicyService.NormalizeBaseItemId(candidate.Item.ItemId));

    // Preserve exact source-slot validation. A destination is a different native
    // object: compare its item data, never its vtable, padding or former location.
    internal static unsafe bool Matches(InventoryItem current, InventoryItem expected, bool allowMovedSlot = false)
    {
        if (!allowMovedSlot)
            return new ReadOnlySpan<byte>(&current, sizeof(InventoryItem)).SequenceEqual(new ReadOnlySpan<byte>(&expected, sizeof(InventoryItem)));
        return !current.IsSymbolic && !expected.IsSymbolic
            && current.ItemId == expected.ItemId && current.Quantity == expected.Quantity
            && current.Flags == expected.Flags && current.Condition == expected.Condition
            && current.SpiritbondOrCollectability == expected.SpiritbondOrCollectability
            && current.CrafterContentId == expected.CrafterContentId && current.GlamourId == expected.GlamourId
            && current.EventId.Id == expected.EventId.Id
            && current.Materia.SequenceEqual(expected.Materia)
            && current.MateriaGrades.SequenceEqual(expected.MateriaGrades)
            && current.Stains.SequenceEqual(expected.Stains);
    }
}
