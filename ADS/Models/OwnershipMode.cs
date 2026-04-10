namespace ADS.Models;

public enum OwnershipMode
{
    Idle = 0,
    Observing = 1,
    OwnedStartOutside = 2,
    OwnedStartInside = 3,
    OwnedResumeInside = 4,
    Leaving = 5,
    Failed = 6,
}
