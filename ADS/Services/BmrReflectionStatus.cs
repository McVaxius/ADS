namespace ADS.Services;

public sealed class BmrReflectionStatus
{
    public bool ToolsEnabled { get; init; }
    public bool BmrInstalled { get; init; }
    public bool BmrLoaded { get; init; }
    public string BmrName { get; init; } = string.Empty;
    public string BmrInternalName { get; init; } = string.Empty;
    public string? BmrVersion { get; init; }
    public bool ReflectionReady { get; init; }
    public string ReflectionState { get; init; } = "Disabled";
    public string? Error { get; init; }
    public bool QueenDesiredDisabled { get; init; }
    public bool HuntsDesiredDisabled { get; init; }
    public bool MaxLoadDistanceDesiredMinimized { get; init; }
    public float MinimizedMaxLoadDistance { get; init; }
    public float? CurrentMaxLoadDistance { get; init; }
    public bool HasCapturedMaxLoadDistance { get; init; }
    public float? CapturedMaxLoadDistance { get; init; }
    public bool QueenActuallyDisabled { get; init; }
    public bool HuntsActuallyDisabled { get; init; }
    public int DisabledRegistryEntryCount { get; init; }
    public int RemovedLiveModuleCount { get; init; }
    public int RestoredRegistryEntryCount { get; init; }
    public int RegisteredModuleCount { get; init; }
    public int KnownHuntModuleCount { get; init; }
    public string LastAction { get; init; } = "No reflection action yet.";
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}
