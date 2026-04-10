namespace ADS.Models;

public enum PlannerMode
{
    IdleObserve = 0,
    UnsupportedDuty = 1,
    UnsafeTransition = 2,
    Combat = 3,
    Recovery = 4,
    Progression = 5,
}
