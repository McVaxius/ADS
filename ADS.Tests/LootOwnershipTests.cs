using ADS.Services;
using AethertekUI.Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Xunit;

namespace ADS.Tests;

public sealed class LootOwnershipTests
{
    [Theory]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Unknown, RollResult.Passed)]
    [InlineData(RollResult.Greeded, true, XaItemOwnershipState.Unknown, RollResult.Greeded)]
    [InlineData(RollResult.Needed, true, XaItemOwnershipState.Unknown, RollResult.Needed)]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Owned, RollResult.Passed)]
    [InlineData(RollResult.Greeded, false, XaItemOwnershipState.Missing, RollResult.Greeded)]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Missing, RollResult.Needed)]
    public void OnlyEnabledConfirmedMissingOverridesConfiguredMode(RollResult configured, bool enabled,
        XaItemOwnershipState ownership, RollResult expected)
        => Assert.Equal(expected, LootAutomationService.MissingDesired(configured, enabled, ownership));

    [Theory]
    [InlineData(RollResult.Needed, RollResult.Needed)]
    [InlineData(RollResult.Greeded, RollResult.Greeded)]
    [InlineData(RollResult.Passed, RollResult.Passed)]
    public void MissingOverrideStillRespectsLiveRollCap(RollResult liveCap, RollResult expected)
    {
        var desired = LootAutomationService.MissingDesired(RollResult.Passed, true, XaItemOwnershipState.Missing);
        Assert.Equal(expected, LootAutomationService.ResultMerge(desired, liveCap));
    }
}
