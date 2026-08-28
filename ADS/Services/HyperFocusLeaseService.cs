using System.Text.Json;
using Dalamud.Plugin;

namespace ADS.Services;

public sealed class HyperFocusLeaseService
{
    private const int ContractVersion = 1;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string, string> acquire;
    private readonly Func<string, string> heartbeat;
    private readonly Func<string, string> release;
    private readonly Func<string> status;
    private readonly Func<DateTime> utcNow;
    private readonly string sessionToken = Guid.NewGuid().ToString("N");
    private DateTime nextHeartbeatUtc = DateTime.MinValue;

    public HyperFocusLeaseService(
        Func<string, string> acquire,
        Func<string, string> heartbeat,
        Func<string, string> release,
        Func<string> status,
        Func<DateTime>? utcNow = null)
    {
        this.acquire = acquire;
        this.heartbeat = heartbeat;
        this.release = release;
        this.status = status;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsActive { get; private set; }
    public string LastStatus { get; private set; } = "No active FrenRider hyper-focus lease.";
    public string LastProviderStatusJson { get; private set; } = string.Empty;

    public static HyperFocusLeaseService Create(IDalamudPluginInterface pluginInterface)
    {
        var acquire = pluginInterface.GetIpcSubscriber<string, string>("FrenRider.ADS.HyperFocus.Acquire");
        var heartbeat = pluginInterface.GetIpcSubscriber<string, string>("FrenRider.ADS.HyperFocus.Heartbeat");
        var release = pluginInterface.GetIpcSubscriber<string, string>("FrenRider.ADS.HyperFocus.Release");
        var status = pluginInterface.GetIpcSubscriber<string>("FrenRider.ADS.HyperFocus.Status");
        return new HyperFocusLeaseService(acquire.InvokeFunc, heartbeat.InvokeFunc, release.InvokeFunc, status.InvokeFunc);
    }

    public bool EnsureLease(out string reason)
    {
        var now = utcNow();
        if (!IsActive && now < nextHeartbeatUtc)
        {
            reason = LastStatus;
            return false;
        }

        var operation = !IsActive ? acquire : now >= nextHeartbeatUtc ? heartbeat : null;
        if (operation is null)
        {
            reason = LastStatus;
            return true;
        }

        if (!TryInvoke(operation, out var response))
        {
            IsActive = false;
            nextHeartbeatUtc = now + HeartbeatInterval;
            reason = LastStatus;
            return false;
        }

        IsActive = response.Ok;
        LastStatus = response.Reason;
        nextHeartbeatUtc = now + HeartbeatInterval;
        reason = LastStatus;
        return response.Ok;
    }

    public void Release(string reason)
    {
        if (!IsActive)
            return;

        if (TryInvoke(release, out var response))
            LastStatus = response.Reason;
        else
            LastStatus = $"Hyper-focus release after {reason} could not reach FrenRider: {LastStatus}";

        IsActive = false;
        nextHeartbeatUtc = DateTime.MinValue;
    }

    public string RefreshStatus()
    {
        try
        {
            LastProviderStatusJson = status();
        }
        catch (Exception ex)
        {
            LastProviderStatusJson = string.Empty;
            LastStatus = $"FrenRider hyper-focus status unavailable: {ex.Message}";
        }

        return LastProviderStatusJson;
    }

    private bool TryInvoke(Func<string, string> operation, out HyperFocusLeaseResponse response)
    {
        try
        {
            var request = JsonSerializer.Serialize(new HyperFocusLeaseRequest(sessionToken), JsonOptions);
            response = JsonSerializer.Deserialize<HyperFocusLeaseResponse>(operation(request), JsonOptions)
                       ?? new HyperFocusLeaseResponse(false, "FrenRider returned an empty hyper-focus lease response.", 0);
            if (response.ContractVersion != ContractVersion)
            {
                response = new HyperFocusLeaseResponse(
                    false,
                    $"FrenRider hyper-focus contract version {response.ContractVersion} is unsupported.",
                    response.ContractVersion);
            }

            return true;
        }
        catch (Exception ex)
        {
            response = new HyperFocusLeaseResponse(false, $"FrenRider hyper-focus IPC unavailable: {ex.Message}", 0);
            LastStatus = response.Reason;
            return false;
        }
    }

    private sealed record HyperFocusLeaseRequest(string SessionToken);
    private sealed record HyperFocusLeaseResponse(bool Ok, string Reason, int ContractVersion);
}
