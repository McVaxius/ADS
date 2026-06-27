using ADS.Services;

namespace ADS.Tests;

public sealed class RemoteJsonUpdateServiceTests
{
    [Fact]
    public void FreshSharedRemoteFilesSkipRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromHours(23)))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.False(decision.ShouldRefresh);
        Assert.Empty(decision.MissingFiles);
        Assert.Empty(decision.StaleFiles);
        Assert.Equal("cache files are younger than 24h", decision.Status);
    }

    [Fact]
    public void MissingSharedRemoteFilesRequestRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, false, DateTime.MinValue))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.True(decision.ShouldRefresh);
        Assert.Equal(RemoteJsonUpdateService.RemoteCacheFileNames, decision.MissingFiles);
        Assert.Empty(decision.StaleFiles);
        Assert.Contains(RemoteJsonUpdateService.DutyMaturityFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.TreasureRoutesFileName, decision.Status);
    }

    [Fact]
    public void StaleSharedRemoteFilesRequestRefresh()
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var states = RemoteJsonUpdateService.RemoteCacheFileNames
            .Select(fileName => new RemoteJsonCacheFileState(fileName, true, now - TimeSpan.FromHours(25)))
            .ToList();

        var decision = RemoteJsonUpdateService.DecideRefresh(states, now, RemoteJsonUpdateService.RefreshInterval);

        Assert.True(decision.ShouldRefresh);
        Assert.Empty(decision.MissingFiles);
        Assert.Equal(RemoteJsonUpdateService.RemoteCacheFileNames, decision.StaleFiles);
        Assert.Contains(RemoteJsonUpdateService.DutyMaturityFileName, decision.Status);
        Assert.Contains(RemoteJsonUpdateService.TreasureRoutesFileName, decision.Status);
    }
}
