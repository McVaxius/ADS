using FFXIVClientStructs.FFXIV.Client.Game;

namespace ADS.Models;

public enum GearCleanupScope { Armoury, Inventory, Both }

public sealed record GearCleanupCandidate(
    InventoryType Container, ushort Slot, InventoryItem Item, string Name,
    bool WhiteEquipment, bool NpcBuyable);

public sealed record GearCleanupSelection(
    ulong CharacterId, GearCleanupScope Scope, IReadOnlyList<GearCleanupCandidate> Items);

internal enum GearSaleState { Ready, Pending, Unsupported }

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

    internal static IEnumerable<InventoryType> Containers(GearCleanupScope scope)
        => scope switch { GearCleanupScope.Armoury => Armoury, GearCleanupScope.Inventory => Bags, _ => Armoury.Concat(Bags) };

    internal static bool IsEligible(GearCleanupCandidate candidate, GearCleanupScope scope, bool selling, IReadOnlySet<uint> protectedItems)
        => Containers(scope).Contains(candidate.Container) && candidate.WhiteEquipment
            && !candidate.Item.IsSymbolic && candidate.Item.ItemId != 0 && candidate.Item.Quantity == 1
            && (!selling || candidate.NpcBuyable)
            && !protectedItems.Contains(ADS.Services.DesynthPolicyService.NormalizeBaseItemId(candidate.Item.ItemId));

    // Compare the confirmed native slot directly, including quality, materia and item state.
    // No inventory fingerprint or persisted identity is introduced.
    internal static unsafe bool Matches(InventoryItem current, InventoryItem expected, bool allowMovedSlot = false)
    {
        if (allowMovedSlot) { current.Container = expected.Container; current.Slot = expected.Slot; }
        return new ReadOnlySpan<byte>(&current, sizeof(InventoryItem)).SequenceEqual(new ReadOnlySpan<byte>(&expected, sizeof(InventoryItem)));
    }
}
