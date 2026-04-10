namespace ADS.Models;

public enum ExecutionPhase
{
    Idle = 0,
    ObservingOnly = 1,
    OutsideQueue = 2,
    AwaitingSupportedPilotDuty = 3,
    TransitionHold = 4,
    CombatHold = 5,
    ReadyForMonsterObjective = 6,
    NavigatingToMonsterObjective = 7,
    ReadyForInteractableObjective = 8,
    NavigatingToInteractableObjective = 9,
    ApproachingInteractableObjective = 10,
    AttemptingInteractableObjective = 11,
    RecoveryHint = 12,
    NavigatingToRecoveryObjective = 13,
    WaitingForTruth = 14,
    LeavingDuty = 15,
    Failure = 16,
    FrontierHint = 17,
    NavigatingToFrontierObjective = 18,
    ReadyForFollowObjective = 19,
    NavigatingToFollowObjective = 20,
    NavigatingToMapXzDestination = 21,
    MapXzDestinationHint = 22,
}
