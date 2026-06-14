namespace ADS.Models;

public static class DutyOwnershipPolicy
{
    public static bool IsDutyOwned(bool inInstancedDuty, OwnershipMode ownershipMode)
        => inInstancedDuty
           && ownershipMode is OwnershipMode.OwnedStartOutside
               or OwnershipMode.OwnedStartInside
               or OwnershipMode.OwnedResumeInside
               or OwnershipMode.Leaving;
}
