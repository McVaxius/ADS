using System.Text.Json;
using ADS.Services;
using AethertekUI.Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Xunit;

namespace ADS.Tests;

public sealed class LootOwnershipTests
{
    [Theory]
    [InlineData(RollResult.Needed, 0, RollResult.Passed)]
    [InlineData(RollResult.Greeded, 0, RollResult.Passed)]
    [InlineData(RollResult.Passed, 0, RollResult.Passed)]
    [InlineData(RollResult.Needed, 1, RollResult.Needed)]
    [InlineData(RollResult.Greeded, null, RollResult.Greeded)]
    [InlineData(RollResult.Needed, null, RollResult.Needed)]
    public void OnlyConfirmedZeroMainBagCapacityForcesPass(RollResult desired, int? emptySlots, RollResult expected)
        => Assert.Equal(expected, LootAutomationService.ApplyInventoryCapacity(desired, emptySlots));

    [Fact]
    public void FullBagWinsAfterMissingCollectibleOrGlamourNeedOverride()
    {
        var desired = LootAutomationService.MissingDesired(RollResult.Passed, true, XaItemOwnershipState.Missing);
        Assert.Equal(RollResult.Needed, desired);
        Assert.Equal(RollResult.Passed, LootAutomationService.ApplyInventoryCapacity(desired, 0));
    }

    [Theory]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Unknown, RollResult.Passed)]
    [InlineData(RollResult.Greeded, true, XaItemOwnershipState.Unknown, RollResult.Greeded)]
    [InlineData(RollResult.Needed, true, XaItemOwnershipState.Unknown, RollResult.Needed)]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Owned, RollResult.Passed)]
    [InlineData(RollResult.Greeded, false, XaItemOwnershipState.Missing, RollResult.Greeded)]
    [InlineData(RollResult.Passed, true, XaItemOwnershipState.Missing, RollResult.Needed)]
    public void OnlyEnabledMissingOverridesConfiguredMode(RollResult configured, bool enabled,
        XaItemOwnershipState ownership, RollResult expected)
        => Assert.Equal(expected, LootAutomationService.MissingDesired(configured, enabled, ownership));

    [Fact]
    public void MissingRecordedCopyEnablesOverrideButPersistedCopyPreservesConfiguredRoll()
    {
        var now = DateTimeOffset.UtcNow;
        var state = XaItemOwnership.QueryStates(_ => "{}", _ => "{}", new uint[] { 100 }, 42, now)[100];
        Assert.Equal(RollResult.Needed, LootAutomationService.MissingDesired(RollResult.Passed, true, state));
        state = XaItemOwnership.QueryStates(_ => "{\"version\":2,\"ready\":true,\"character\":{\"contentId\":42},\"rows\":[{\"itemId\":100,\"ownerContentId\":42,\"source\":\"glamourDresser\",\"quantity\":1,\"lastSeenUtc\":\"2020-01-01T00:00:00Z\",\"snapshotQuality\":\"Stale\"}]}",
            _ => "{}", new uint[] { 100 }, 42, now)[100];
        Assert.Equal(RollResult.Passed, LootAutomationService.MissingDesired(RollResult.Passed, true, state));
    }

    [Theory]
    [InlineData("Inventory 1")]
    [InlineData("Inventory 2")]
    [InlineData("Inventory 3")]
    [InlineData("Inventory 4")]
    [InlineData("Equipped")]
    [InlineData("Armoury - Main Hand")]
    [InlineData("Armoury - Off Hand")]
    [InlineData("Armoury - Head")]
    [InlineData("Armoury - Body")]
    [InlineData("Armoury - Hands")]
    [InlineData("Armoury - Legs")]
    [InlineData("Armoury - Feet")]
    [InlineData("Armoury - Earring")]
    [InlineData("Armoury - Necklace")]
    [InlineData("Armoury - Bracelet")]
    [InlineData("Armoury - Ring")]
    [InlineData("Armoury - Soul Crystal")]
    [InlineData("Saddlebag 1")]
    [InlineData("Saddlebag 2")]
    [InlineData("Premium Saddlebag 1")]
    [InlineData("Premium Saddlebag 2")]
    public void RecordedInventoryLocationsPreserveConfiguredRoll(string containerName)
    {
        var now = DateTimeOffset.UtcNow;
        var response = JsonSerializer.Serialize(new
        {
            version = 2, ready = true, character = new { contentId = 42UL },
            rows = new[] { new { itemId = 100U, ownerContentId = 42UL, source = "inventory",
                containerName, quantity = 1 } },
        });
        var state = XaItemOwnership.QueryStates(request =>
        {
            using var document = JsonDocument.Parse(request);
            Assert.Contains(document.RootElement.GetProperty("sources").EnumerateArray(),
                source => source.GetString() == "inventory");
            return response;
        }, _ => "{}", new uint[] { 100 }, 42, now)[100];
        Assert.Equal(XaItemOwnershipState.Owned, state);
        Assert.Equal(RollResult.Passed, LootAutomationService.MissingDesired(RollResult.Passed, true, state));
    }

    [Theory]
    [InlineData("retainer")]
    [InlineData("armoire")]
    [InlineData("glamourDresser")]
    public void RecordedStorageLocationsPreserveConfiguredRoll(string source)
    {
        var response = JsonSerializer.Serialize(new
        {
            version = 2, ready = true, character = new { contentId = 42UL },
            rows = new[] { new { itemId = 100U, ownerContentId = 42UL, source, quantity = 1 } },
        });
        var state = XaItemOwnership.QueryStates(_ => response, _ => "{}", new uint[] { 100 },
            42, DateTimeOffset.UtcNow)[100];
        Assert.Equal(XaItemOwnershipState.Owned, state);
        Assert.Equal(RollResult.Passed, LootAutomationService.MissingDesired(RollResult.Passed, true, state));
    }

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
