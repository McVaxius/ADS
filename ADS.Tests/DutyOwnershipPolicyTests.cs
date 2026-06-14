using ADS.Models;

namespace ADS.Tests;

public sealed class DutyOwnershipPolicyTests
{
    public static TheoryData<OwnershipMode, bool> InsideDutyCases => new()
    {
        { OwnershipMode.Idle, false },
        { OwnershipMode.Observing, false },
        { OwnershipMode.OwnedStartOutside, true },
        { OwnershipMode.OwnedStartInside, true },
        { OwnershipMode.OwnedResumeInside, true },
        { OwnershipMode.Leaving, true },
        { OwnershipMode.Failed, false },
    };

    [Theory]
    [MemberData(nameof(InsideDutyCases))]
    public void InsideDutyOwnershipMatchesMode(OwnershipMode mode, bool expected)
        => Assert.Equal(expected, DutyOwnershipPolicy.IsDutyOwned(true, mode));

    [Theory]
    [MemberData(nameof(InsideDutyCases))]
    public void OutsideDutyNeverReportsOwned(OwnershipMode mode, bool _)
        => Assert.False(DutyOwnershipPolicy.IsDutyOwned(false, mode));
}
