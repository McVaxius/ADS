using System.Numerics;
using ADS.Models;

namespace ADS.Tests;

public sealed class CrystalTowerMechanicTests
{
    [Fact]
    public void HolderElectionUsesStableIdentityAcrossDifferentClientRosterOrders()
    {
        var party = Party();
        var expected = new ulong[] { 20, 30, 40, 50 };
        Assert.Equal(expected, CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party));
        Assert.Equal(expected, CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party.Reverse().ToArray()));
        Assert.Equal(expected, CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party.Skip(3).Concat(party.Take(3)).ToArray()));
    }

    [Fact]
    public void HolderElectionFillsFromMeleeAndTankOnlyWhenNeededAndRequiresOnlyFourMembers()
    {
        CrystalTowerMember[] party =
        [
            new(9, CrystalTowerRole.Tank, true, Pad),
            new(7, CrystalTowerRole.Melee, true, Pad),
            new(8, CrystalTowerRole.Healer, true, Pad),
            new(6, CrystalTowerRole.Ranged, true, Pad),
        ];
        Assert.Equal(new ulong[] { 6, 8, 7, 9 }, CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party));
    }

    [Fact]
    public void UnknownIncompleteDuplicateAndInsufficientRostersCannotElect()
    {
        var party = Party();
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(false, party));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true, null));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party.Take(3).ToArray()));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true,
            party.Select(member => member.Id == 20 ? member with { Role = CrystalTowerRole.Unknown } : member).ToArray()));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true,
            party.Select(member => member.Id == 20 ? member with { Alive = null } : member).ToArray()));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true, party.Append(party[0]).ToArray()));
        Assert.Empty(CrystalTowerMechanicPolicy.SelectAtomosHolders(true,
            party.Select(member => member with { Alive = member.Id == 10 }).ToArray()));
    }

    [Theory]
    [InlineData(CrystalTowerAlliance.A, CrystalTowerAlliance.B, 244f)]
    [InlineData(CrystalTowerAlliance.B, CrystalTowerAlliance.C, 280f)]
    [InlineData(CrystalTowerAlliance.C, CrystalTowerAlliance.A, 315f)]
    public void AssignedHoldersStayUntilCrossLaneBossDiesEvenWhenOwnBossIsDead(
        CrystalTowerAlliance alliance, CrystalTowerAlliance controlled, float z)
    {
        var pad = new Vector2(220, z);
        var party = Party().Select(member => member with { Position = pad }).ToArray();
        var bosses = Bosses().Select(boss => boss.Lane == alliance ? boss with { Alive = false } : boss).ToArray();
        var holding = Evaluate(alliance, 20, party, pad, bosses);
        Assert.Equal(CrystalTowerAtomosAction.HoldPad, holding.Action);
        Assert.Equal(pad, holding.PadXZ);
        Assert.False(holding.CanAttackAtomos);

        bosses = bosses.Select(boss => boss.Lane == controlled ? boss with { Alive = false } : boss).ToArray();
        var released = Evaluate(alliance, 20, party, pad, bosses);
        Assert.Equal(CrystalTowerAtomosAction.Released, released.Action);
        Assert.Null(released.PadXZ);
    }

    [Fact]
    public void DeadMissingAndUnknownAssignedHoldersWaitWithoutReplacingFrozenAssignments()
    {
        var party = Party();
        foreach (var changed in new[]
                 {
                     party.Select(member => member.Id == 20 ? member with { Alive = false } : member).ToArray(),
                     party.Where(member => member.Id != 20).ToArray(),
                     party.Select(member => member.Id == 20 ? member with { Alive = null } : member).ToArray(),
                 })
        {
            var decision = Evaluate(CrystalTowerAlliance.A, 10, changed, Pad, Bosses());
            Assert.Equal(CrystalTowerAtomosAction.Wait, decision.Action);
            Assert.Equal(Holders, decision.HolderIds);
            Assert.False(decision.CanAttackAtomos);
        }
        Assert.Equal(CrystalTowerAtomosAction.HoldPad,
            Evaluate(CrystalTowerAlliance.A, 20, party, Pad, Bosses()).Action);
    }

    [Fact]
    public void AttackersWaitForFourLivingOccupantsAndActualLocalRingVulnerability()
    {
        var party = Party();
        Assert.Equal(CrystalTowerAtomosAction.AttackAtomos,
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses()).Action);
        var away = party.Select(member => member.Id == 50
            ? member with { Position = Pad + new Vector2(4.01f, 0) } : member).ToArray();
        var waiting = Evaluate(CrystalTowerAlliance.A, 10, away, Pad, Bosses());
        Assert.Equal(3, waiting.PadOccupants);
        Assert.Equal(CrystalTowerAtomosAction.Wait, waiting.Action);
        Assert.False(waiting.CanAttackAtomos);
        Assert.Equal(CrystalTowerAtomosAction.AttackAdds,
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses(), ring: false).Action);
        Assert.Equal(CrystalTowerAtomosAction.Wait,
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses(), ring: null).Action);
    }

    [Fact]
    public void PadOccupancyIncludesRadiusBoundaryButNeverCountsCorpses()
    {
        var party = Party().Select(member => member.Id == 50
            ? member with { Position = Pad + new Vector2(4, 0) } : member).ToArray();
        Assert.Equal(4, Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses()).PadOccupants);
        var corpseOnPad = party.Select(member => member.Id == 60
            ? member with { Alive = false, Position = Pad } : member).ToArray();
        Assert.Equal(4, Evaluate(CrystalTowerAlliance.A, 10, corpseOnPad, Pad, Bosses()).PadOccupants);
    }

    [Fact]
    public void UnknownAllianceBossPadRosterPositionAndDeadLocalMemberFailClosed()
    {
        var party = Party();
        CrystalTowerAtomosDecision[] invalid =
        [
            Evaluate(CrystalTowerAlliance.Unknown, 10, party, Pad, Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party, null, Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party, new Vector2(float.NaN, 244), Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party, new Vector2(220, 315), Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses().Take(2).ToArray()),
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses().Select(boss => boss with { Alive = null }).ToArray()),
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, [Bosses()[0], Bosses()[0], Bosses()[2]]),
            Evaluate(CrystalTowerAlliance.A, 10, party, Pad, Bosses(), complete: false),
            Evaluate(CrystalTowerAlliance.A, 99, party, Pad, Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party.Select(member => member.Id == 10 ? member with { Alive = false } : member).ToArray(), Pad, Bosses()),
            Evaluate(CrystalTowerAlliance.A, 10, party.Select(member => member.Id == 60 ? member with { Position = null } : member).ToArray(), Pad, Bosses()),
        ];
        Assert.All(invalid, decision =>
        {
            Assert.Equal(CrystalTowerAtomosAction.Wait, decision.Action);
            Assert.False(decision.CanAttackAtomos);
            Assert.Null(decision.PadXZ);
        });
    }

    [Theory]
    [InlineData(244f, CrystalTowerAlliance.A)]
    [InlineData(265f, CrystalTowerAlliance.A)]
    [InlineData(265.01f, CrystalTowerAlliance.B)]
    [InlineData(280f, CrystalTowerAlliance.B)]
    [InlineData(294.99f, CrystalTowerAlliance.B)]
    [InlineData(295f, CrystalTowerAlliance.C)]
    [InlineData(315f, CrystalTowerAlliance.C)]
    public void AtomosLaneClassificationUsesVerifiedWorldZBoundaries(float z, CrystalTowerAlliance expected)
        => Assert.Equal(expected, CrystalTowerMechanicPolicy.GetAtomosLane(new Vector2(253, z)));

    [Fact]
    public void ThanatosRequiresActualAstralRealignmentAndLivingParticipants()
    {
        Assert.True(CrystalTowerMechanicPolicy.CanAttackThanatos(true, true, [398]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackThanatos(true, true, [397, 399]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackThanatos(true, true, null));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackThanatos(false, true, [398]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackThanatos(true, null, [398]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackThanatos(true, false, [398]));
    }

    [Fact]
    public void BombRequiresThreeDistinctConfirmedDeadVassagoAndNeverTreatsMissingAsDead()
    {
        CrystalTowerActorState[] dead = [new(101, false), new(102, false), new(103, false)];
        Assert.True(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, dead));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, [dead[0], dead[1], new(103, true)]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, [dead[0], dead[1], new(103, null)]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, dead.Take(2).ToArray()));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, [dead[0], dead[0], dead[2]]));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, true, null));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(false, true, dead));
        Assert.False(CrystalTowerMechanicPolicy.CanAttackBomb(true, false, dead));
    }

    [Theory]
    [InlineData(CrystalTowerAlliance.A, -148.65f, 191.975f)]
    [InlineData(CrystalTowerAlliance.B, -110f, 221.59f)]
    [InlineData(CrystalTowerAlliance.C, -71.35f, 191.975f)]
    public void AncientFlareRetreatPersistsUntilVerifiedClearAndUsesOnlyVerifiedXz(
        CrystalTowerAlliance alliance, float x, float z)
    {
        var retreat = CrystalTowerMechanicPolicy.EvaluatePhlegethon(alliance, true, true, false);
        Assert.Equal(CrystalTowerRetreatAction.Retreat, retreat.Action);
        Assert.Equal(new Vector2(x, z), retreat.DestinationXZ);
        // The owner retains retreatActive after cast disappearance; no clear observation is invented here.
        Assert.Equal(CrystalTowerRetreatAction.Retreat,
            CrystalTowerMechanicPolicy.EvaluatePhlegethon(alliance, true, true, null).Action);
        var clear = CrystalTowerMechanicPolicy.EvaluatePhlegethon(alliance, true, true, true);
        Assert.Equal(CrystalTowerRetreatAction.Released, clear.Action);
        Assert.Null(clear.DestinationXZ);
    }

    [Fact]
    public void AncientFlareUnknownDeadAndUncorrelatedClearCannotResumeMovement()
    {
        Assert.Equal(CrystalTowerRetreatAction.Continue,
            CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.A, true, false, false).Action);
        foreach (var decision in new[]
                 {
                     CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.Unknown, true, true, false),
                     CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.A, false, true, false),
                     CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.A, true, null, false),
                     CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.A, true, false, null),
                     CrystalTowerMechanicPolicy.EvaluatePhlegethon(CrystalTowerAlliance.A, true, false, true),
                 })
        {
            Assert.Equal(CrystalTowerRetreatAction.Wait, decision.Action);
            Assert.Null(decision.DestinationXZ);
        }
    }

    private static readonly Vector2 Pad = new(220, 244);
    private static readonly ulong[] Holders = [20, 30, 40, 50];

    private static CrystalTowerMember[] Party() =>
    [
        new(10, CrystalTowerRole.Tank, true, new Vector2(245, 244)),
        new(20, CrystalTowerRole.Healer, true, Pad),
        new(30, CrystalTowerRole.Ranged, true, Pad),
        new(40, CrystalTowerRole.Healer, true, Pad),
        new(50, CrystalTowerRole.Ranged, true, Pad),
        new(60, CrystalTowerRole.Melee, true, new Vector2(245, 244)),
    ];

    private static CrystalTowerBossState[] Bosses() =>
    [
        new(CrystalTowerAlliance.A, true),
        new(CrystalTowerAlliance.B, true),
        new(CrystalTowerAlliance.C, true),
    ];

    private static CrystalTowerAtomosDecision Evaluate(
        CrystalTowerAlliance alliance,
        ulong localId,
        IReadOnlyList<CrystalTowerMember> party,
        Vector2? pad,
        IReadOnlyList<CrystalTowerBossState> bosses,
        bool? ring = true,
        bool complete = true)
        => CrystalTowerMechanicPolicy.EvaluateAtomos(alliance, localId, complete, party, Holders, pad, bosses, ring);
}
