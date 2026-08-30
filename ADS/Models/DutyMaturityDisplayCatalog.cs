using System.Numerics;

namespace ADS.Models;

public static class DutyMaturityDisplayCatalog
{
    public static readonly DutyClearanceStatus[] ClearanceValues = Enum.GetValues<DutyClearanceStatus>();
    public static readonly string[] ClearanceLabels = ClearanceValues.Select(GetClearanceLabel).ToArray();

    public static string GetClearanceLabel(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => "M1",
            DutyClearanceStatus.OnePlayerDutySupport => "M2",
            DutyClearanceStatus.FourPlayerSyncCleared => "M3",
            _ => "M0",
        };

    public static Vector4 GetClearanceColor(DutyClearanceStatus status)
        => status switch
        {
            DutyClearanceStatus.OnePlayerUnsyncCleared => new Vector4(0.35f, 0.62f, 1.0f, 1f),
            DutyClearanceStatus.OnePlayerDutySupport => new Vector4(1.0f, 0.86f, 0.24f, 1f),
            DutyClearanceStatus.FourPlayerSyncCleared => new Vector4(0.42f, 0.94f, 0.64f, 1f),
            _ => new Vector4(1.0f, 0.36f, 0.32f, 1f),
        };

    public static Vector4 GetMsqColor(bool isMainScenario)
        => isMainScenario
            ? new Vector4(0.78f, 0.72f, 1.0f, 1f)
            : new Vector4(0.62f, 0.62f, 0.62f, 1f);
}
