using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class DutyOwnershipPolicyTests
{
    [Fact]
    public void RaidStateRequiresMatchingDutyAndFailsClosedAfterKnownEnableUntilExplicitDisable()
    {
        var context = TestDutyContextFactory.Create(territoryId: 777, cfcId: 888);
        var policy = new RaidDutyPolicy();
        policy.QueryRuntimeStateJson = () => "{\"schemaVersion\":1,\"enabled\":true,\"sourceReadable\":true,\"recoveryGeneration\":0,\"sessionId\":\"synthetic-session\",\"territoryTypeId\":777,\"contentFinderConditionId\":888,\"adsLootEnabled\":true,\"automaticExitAllowedNow\":true,\"awaitingCountdown\":false,\"automaticStartAllowedNow\":true}";
        Assert.True(policy.Read(context).Matches(context));
        Assert.False(policy.Read(context).Matches(TestDutyContextFactory.Create(territoryId: 778, cfcId: 888)));
        policy.QueryRuntimeStateJson = () => throw new InvalidOperationException("Endpoint unavailable");
        var unavailable = policy.Read(context);
        Assert.True(unavailable.Enabled);
        Assert.False(unavailable.Readable);
        Assert.False(unavailable.AdsLootEnabled);
        Assert.False(unavailable.AutomaticExitAllowedNow);
        Assert.False(unavailable.AutomaticStartAllowedNow);
        policy.QueryRuntimeStateJson = () => "{\"schemaVersion\":1,\"enabled\":false}";
        Assert.False(policy.Read(context).Enabled);
        policy.QueryRuntimeStateJson = () => "{\"schemaVersion\":1,\"enabled\":true}"; // Partial enabled state grants no action.
        Assert.True(policy.Read(context).Enabled);
        Assert.False(policy.Read(context).Readable);
        Assert.False(policy.Read(TestDutyContextFactory.Create(loggedIn: false)).Enabled);
    }

    [Fact]
    public void MissingRaidEndpointHoldsItsKnownDutyThenReleasesAtDepartureForTheNextOrdinaryDuty()
    {
        var context = TestDutyContextFactory.Create(territoryId: 777, cfcId: 888);
        var policy = new RaidDutyPolicy
        {
            QueryRuntimeStateJson = () => "{\"schemaVersion\":1,\"enabled\":true,\"sourceReadable\":true,\"recoveryGeneration\":0,\"sessionId\":\"synthetic-session\",\"territoryTypeId\":777,\"contentFinderConditionId\":888,\"adsLootEnabled\":true,\"automaticExitAllowedNow\":false,\"awaitingCountdown\":false,\"automaticStartAllowedNow\":false}",
        };
        Assert.True(policy.Read(context).Matches(context));
        policy.QueryRuntimeStateJson = () => null; // FR disabled/disposed without a final observable false snapshot.
        Assert.True(policy.Read(context).Enabled);
        Assert.False(policy.Read(context).Readable);
        Assert.False(policy.Read(TestDutyContextFactory.Create(inDuty: false, territoryId: 129, cfcId: 0)).Enabled);
        var next = TestDutyContextFactory.Create(territoryId: 778, cfcId: 889);
        Assert.False(policy.Read(next).Enabled);
    }

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
