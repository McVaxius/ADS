using ADS.Models;

namespace ADS.Tests;

public sealed class DutyOwnershipPolicyTests
{
    [Fact]
    public void OlderSettingsKeepAutoDutyLoadedAndExplicitOptInRoundTrips()
    {
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            "{\"OpenQuickControlsOnLoad\":true}")!;
        Assert.False(restored.DisableAutoDutyOnStart);
        Assert.True(restored.OpenQuickControlsOnLoad);
        restored.DisableAutoDutyOnStart = true;
        var roundTrip = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(restored))!;
        Assert.True(roundTrip.DisableAutoDutyOnStart);
        Assert.True(roundTrip.OpenQuickControlsOnLoad);
    }

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
