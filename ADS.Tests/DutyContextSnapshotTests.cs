using ADS.Models;

namespace ADS.Tests;

public sealed class DutyContextSnapshotTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void InteractionOccupancyRemainsNarrow(bool occupiedInQuestEvent, bool occupiedInEvent, bool expected)
    {
        var context = new DutyContextSnapshot
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = true,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInQuestEvent = occupiedInQuestEvent,
            OccupiedInEvent = occupiedInEvent,
            OccupiedInCutSceneEvent = false,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = 0,
            MapId = 0,
            ContentFinderConditionId = 0,
            CurrentDuty = null,
        };

        Assert.Equal(expected, context.IsInteractionOccupied);
        Assert.False(context.IsUnsafeTransition);
        Assert.False(context.IsTreasureRouteTransitHold);
    }
}
