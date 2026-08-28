using System.Text.Json;
using ADS.Services;

namespace ADS.Tests;

public sealed class HyperFocusLeaseServiceTests
{
    [Fact]
    public void AcquireThenHeartbeatThenReleaseUsesCamelCaseSessionToken()
    {
        var now = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        var acquireCalls = 0;
        var heartbeatCalls = 0;
        var releaseCalls = 0;
        var service = new HyperFocusLeaseService(
            request =>
            {
                acquireCalls++;
                AssertSessionToken(request);
                return Response(ok: true, "acquired");
            },
            request =>
            {
                heartbeatCalls++;
                AssertSessionToken(request);
                return Response(ok: true, "heartbeat");
            },
            request =>
            {
                releaseCalls++;
                AssertSessionToken(request);
                return Response(ok: true, "released");
            },
            () => "{\"contractVersion\":1,\"leaseActive\":true}",
            () => now);

        Assert.True(service.EnsureLease(out var acquireReason));
        Assert.Equal("acquired", acquireReason);
        now = now.AddSeconds(1);
        Assert.True(service.EnsureLease(out _));
        now = now.AddSeconds(2);
        Assert.True(service.EnsureLease(out var heartbeatReason));
        service.Release("test cleanup");

        Assert.Equal(1, acquireCalls);
        Assert.Equal(1, heartbeatCalls);
        Assert.Equal(1, releaseCalls);
        Assert.Equal("heartbeat", heartbeatReason);
        Assert.False(service.IsActive);
        Assert.Contains("leaseActive", service.RefreshStatus());
    }

    [Fact]
    public void ContractMismatchFailsClosed()
    {
        var calls = 0;
        var now = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        var service = new HyperFocusLeaseService(
            _ =>
            {
                calls++;
                return "{\"ok\":true,\"reason\":\"wrong version\",\"contractVersion\":2}";
            },
            _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
            () => "{}",
            () => now);

        Assert.False(service.EnsureLease(out var reason));
        Assert.False(service.EnsureLease(out _));
        Assert.False(service.IsActive);
        Assert.Equal(1, calls);
        Assert.Contains("unsupported", reason, StringComparison.OrdinalIgnoreCase);
    }

    private static string Response(bool ok, string reason)
        => $"{{\"ok\":{ok.ToString().ToLowerInvariant()},\"reason\":\"{reason}\",\"contractVersion\":1}}";

    private static void AssertSessionToken(string request)
    {
        using var document = JsonDocument.Parse(request);
        Assert.True(document.RootElement.TryGetProperty("sessionToken", out var token));
        Assert.False(string.IsNullOrWhiteSpace(token.GetString()));
    }
}
