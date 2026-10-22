using System.Numerics;

namespace ADS.Models;

public enum CrystalTowerAlliance
{
    Unknown,
    A,
    B,
    C,
}

public enum CrystalTowerRole
{
    Unknown,
    Healer,
    Ranged,
    Tank,
    Melee,
}

public readonly record struct CrystalTowerMember(
    ulong Id,
    CrystalTowerRole Role,
    bool? Alive,
    Vector2? Position);

public readonly record struct CrystalTowerBossState(CrystalTowerAlliance Lane, bool? Alive);

public readonly record struct CrystalTowerActorState(ulong Id, bool? Alive);

public enum CrystalTowerAtomosAction
{
    Wait,
    HoldPad,
    AttackAdds,
    AttackAtomos,
    Released,
}

public sealed record CrystalTowerAtomosDecision(
    CrystalTowerAtomosAction Action,
    IReadOnlyList<ulong> HolderIds,
    Vector2? PadXZ,
    int PadOccupants,
    bool CanAttackAtomos,
    string Reason);

public enum CrystalTowerRetreatAction
{
    Wait,
    Continue,
    Retreat,
    Released,
}

public readonly record struct CrystalTowerRetreatDecision(
    CrystalTowerRetreatAction Action,
    Vector2? DestinationXZ,
    string Reason);

public static class CrystalTowerMechanicPolicy
{
    // Mechanics/geometry: awgil/ffxiv_bossmod d409d336edb909b71b25fa8761f88667f5bdce1b,
    // BossMod.Modules/RealmReborn/Alliance/{A12Atomos,A13Thanatos,A14AllaganBomb,A16Phlegethon}.
    // Vector2 always means world X/Z. Movement height and encounter lifetime belong to the caller.
    public const uint AtomosBossDataId = 0x963;
    public const uint ThanatosBossDataId = 0x92E;
    public const uint AllaganBombDataId = 0x967;
    public const uint VassagoDataId = 0x96A;
    public const uint PhlegethonBossDataId = 0x938;
    public const uint AstralRealignmentStatusId = 398;
    public const uint AncientFlareCastActionId = 1730;
    public const int AtomosRequiredHolders = 4;
    public const float AtomosPadRadius = 4f;

    /// <summary>Elect once per encounter, then retain these stable actor identities in the execution owner.</summary>
    public static IReadOnlyList<ulong> SelectAtomosHolders(
        bool rosterComplete,
        IReadOnlyList<CrystalTowerMember>? members)
    {
        if (!IsKnownRoster(rosterComplete, members))
            return Array.Empty<ulong>();

        var holders = members!
            .Where(member => member.Alive == true)
            .OrderBy(member => member.Role is CrystalTowerRole.Healer or CrystalTowerRole.Ranged ? 0 : 1)
            .ThenBy(member => member.Id)
            .Take(AtomosRequiredHolders)
            .Select(member => member.Id)
            .ToArray();
        return holders.Length == AtomosRequiredHolders ? holders : Array.Empty<ulong>();
    }

    public static CrystalTowerAtomosDecision EvaluateAtomos(
        CrystalTowerAlliance alliance,
        ulong localId,
        bool rosterComplete,
        IReadOnlyList<CrystalTowerMember>? members,
        IReadOnlyList<ulong>? assignedHolderIds,
        Vector2? padXZ,
        IReadOnlyList<CrystalTowerBossState>? bosses,
        bool? ringVulnerable)
    {
        var holders = assignedHolderIds?.ToArray() ?? Array.Empty<ulong>();
        CrystalTowerAtomosDecision Wait(string reason) => new(
            CrystalTowerAtomosAction.Wait, holders, null, 0, false, reason);

        if (!IsKnownAlliance(alliance))
            return Wait("Alliance is unknown.");
        if (!IsKnownRoster(rosterComplete, members))
            return Wait("A complete, known party roster is required.");
        if (holders.Length != AtomosRequiredHolders || holders.Any(id => id == 0)
            || holders.Distinct().Count() != AtomosRequiredHolders)
            return Wait("Four distinct assigned pad holders are required.");

        var roster = members!;
        var local = roster.FirstOrDefault(member => member.Id == localId);
        if (localId == 0 || local.Id != localId || local.Alive != true)
            return Wait("The local member is missing or not alive.");
        if (holders.Any(id => !roster.Any(member => member.Id == id && member.Alive == true)))
            return Wait("An assigned holder is missing or not alive; retain the assignment for recovery.");
        if (roster.Any(member => member.Alive == true && !IsFinite(member.Position)))
            return Wait("Living member positions are incomplete.");
        if (!IsFinite(padXZ) || GetAtomosLane(padXZ) != alliance)
            return Wait("The assigned lane's observed pad position is unavailable.");
        if (bosses == null || bosses.Count != 3
            || bosses.Any(boss => !IsKnownAlliance(boss.Lane) || !boss.Alive.HasValue)
            || bosses.Select(boss => boss.Lane).Distinct().Count() != 3)
            return Wait("All three Atomos lane states must be known.");

        var pad = padXZ!.Value;
        var occupants = roster.Count(member => member.Alive == true
            && Vector2.DistanceSquared(member.Position!.Value, pad) <= AtomosPadRadius * AtomosPadRadius);
        var ownBossAlive = bosses.Single(boss => boss.Lane == alliance).Alive == true;
        var controlledBossDead = bosses.Single(boss => boss.Lane == GetAtomosControlledLane(alliance)).Alive == false;
        var canAttack = ownBossAlive && ringVulnerable == true
            && (controlledBossDead || occupants >= AtomosRequiredHolders);

        if (controlledBossDead)
            return new(CrystalTowerAtomosAction.Released, holders, null, occupants, canAttack,
                "The next lane's Atomos is confirmed dead; this pad is released.");
        if (holders.Contains(localId))
            return new(CrystalTowerAtomosAction.HoldPad, holders, pad, occupants, canAttack,
                "Hold the assigned pad until the next lane's Atomos is confirmed dead.");
        if (!ringVulnerable.HasValue)
            return Wait("The local Atomos ring state is unknown.");
        if (occupants < AtomosRequiredHolders)
            return new(CrystalTowerAtomosAction.Wait, holders, null, occupants, false,
                "Four living party members must occupy the assigned pad before a boss pull.");
        return canAttack
            ? new(CrystalTowerAtomosAction.AttackAtomos, holders, null, occupants, true,
                "The assigned pad is occupied and the local Atomos is vulnerable.")
            : new(CrystalTowerAtomosAction.AttackAdds, holders, null, occupants, false,
                "Only adds in the assigned lane are eligible while the local Atomos is unavailable.");
    }

    public static CrystalTowerAlliance GetAtomosLane(Vector2? position)
    {
        if (!IsFinite(position))
            return CrystalTowerAlliance.Unknown;
        var zDistance = position!.Value.Y - 280f;
        return MathF.Abs(zDistance) < 15f
            ? CrystalTowerAlliance.B
            : zDistance < 0f ? CrystalTowerAlliance.A : CrystalTowerAlliance.C;
    }

    public static CrystalTowerAlliance GetAtomosControlledLane(CrystalTowerAlliance alliance) => alliance switch
    {
        CrystalTowerAlliance.A => CrystalTowerAlliance.B,
        CrystalTowerAlliance.B => CrystalTowerAlliance.C,
        CrystalTowerAlliance.C => CrystalTowerAlliance.A,
        _ => CrystalTowerAlliance.Unknown,
    };

    public static uint? GetAtomosPadDataId(CrystalTowerAlliance alliance) => alliance switch
    {
        CrystalTowerAlliance.A => 0x1E8F80,
        CrystalTowerAlliance.B => 0x1E8F81,
        CrystalTowerAlliance.C => 0x1E8F82,
        _ => null,
    };

    public static bool CanAttackThanatos(
        bool? localAlive,
        bool? targetAlive,
        IReadOnlyCollection<uint>? statusIds)
        => localAlive == true && targetAlive == true
           && statusIds?.Contains(AstralRealignmentStatusId) == true;

    public static bool CanAttackBomb(
        bool? localAlive,
        bool? bombAlive,
        IReadOnlyList<CrystalTowerActorState>? vassago)
        => localAlive == true && bombAlive == true
           && vassago is { Count: 3 }
           && vassago.All(actor => actor.Id != 0 && actor.Alive == false)
           && vassago.Select(actor => actor.Id).Distinct().Count() == 3;

    /// <summary>
    /// retreatActive is latched by the owner from a verified cast-1730 start; cast disappearance must not clear it.
    /// Only a verified mechanic-clear signal releases that latch. Unknown inputs never resume movement.
    /// </summary>
    public static CrystalTowerRetreatDecision EvaluatePhlegethon(
        CrystalTowerAlliance alliance,
        bool? localAlive,
        bool? retreatActive,
        bool? verifiedClear)
    {
        if (!IsKnownAlliance(alliance) || localAlive != true || !retreatActive.HasValue)
            return new(CrystalTowerRetreatAction.Wait, null, "Alliance, living local member and retreat phase must be known.");
        if (retreatActive == true)
        {
            if (verifiedClear == true)
                return new(CrystalTowerRetreatAction.Released, null, "Ancient Flare is confirmed cleared.");
            var pad = alliance switch
            {
                CrystalTowerAlliance.A => new Vector2(-148.65f, 191.975f),
                CrystalTowerAlliance.B => new Vector2(-110f, 221.59f),
                _ => new Vector2(-71.35f, 191.975f),
            };
            return new(CrystalTowerRetreatAction.Retreat, pad, "Retreat to the assigned platform and retain it until verified clear.");
        }
        return verifiedClear == false
            ? new(CrystalTowerRetreatAction.Continue, null, "The owner confirms that no Ancient Flare retreat has started.")
            : new(CrystalTowerRetreatAction.Wait, null, "Retreat history and clear signal do not establish an inactive phase.");
    }

    private static bool IsKnownAlliance(CrystalTowerAlliance alliance)
        => alliance is CrystalTowerAlliance.A or CrystalTowerAlliance.B or CrystalTowerAlliance.C;

    private static bool IsKnownRoster(bool complete, IReadOnlyList<CrystalTowerMember>? members)
        => complete && members is { Count: >= AtomosRequiredHolders }
           && members.All(member => member.Id != 0 && member.Alive.HasValue
               && member.Role is CrystalTowerRole.Healer or CrystalTowerRole.Ranged
                   or CrystalTowerRole.Melee or CrystalTowerRole.Tank)
           && members.Select(member => member.Id).Distinct().Count() == members.Count;

    private static bool IsFinite(Vector2? position)
        => position is { } value && float.IsFinite(value.X) && float.IsFinite(value.Y);
}
