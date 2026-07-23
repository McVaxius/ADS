using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class SoloDutyLeaveNoticeServiceTests
{
    [Fact]
    public void ShowsOncePerContinuousSoloDutyEntryAcrossTerritoryChanges()
    {
        var toasts = new List<string>();
        var service = new SoloDutyLeaveNoticeService(toasts.Add);

        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, territoryId: 100, cfcId: 200));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, territoryId: 101, cfcId: 200));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, territoryId: 102, cfcId: 0));

        Assert.Equal([SoloDutyLeaveNoticeService.NoticeText], toasts);
        Assert.Equal("cfc:200", service.LastDiagnosticKey);
    }

    [Fact]
    public void StableExitAndReentryResetTheLatch()
    {
        var toasts = new List<string>();
        var service = new SoloDutyLeaveNoticeService(toasts.Add);
        var solo = TestDutyContextFactory.Create(DutyCategory.Solo);

        service.Update(solo);
        service.Update(TestDutyContextFactory.Create(category: null, inDuty: false));
        service.Update(solo);

        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public void TransitionDoesNotResetOrConsumeEntry()
    {
        var toasts = new List<string>();
        var service = new SoloDutyLeaveNoticeService(toasts.Add);

        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, betweenAreas: true));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo));
        service.Update(TestDutyContextFactory.Create(category: null, inDuty: false, betweenAreas: true));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo));

        Assert.Single(toasts);
    }

    [Fact]
    public void MissingMetadataNonSoloAndDisabledDoNotConsumeNotice()
    {
        var toasts = new List<string>();
        var service = new SoloDutyLeaveNoticeService(toasts.Add);

        service.Update(TestDutyContextFactory.Create(category: null));
        service.Update(TestDutyContextFactory.Create(DutyCategory.FourMan));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, pluginEnabled: false));
        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo));

        Assert.Single(toasts);
    }

    [Fact]
    public void LogoutResetsTheEntry()
    {
        var toasts = new List<string>();
        var service = new SoloDutyLeaveNoticeService(toasts.Add);
        var solo = TestDutyContextFactory.Create(DutyCategory.Solo);
        service.Update(solo);

        service.Update(TestDutyContextFactory.Create(DutyCategory.Solo, loggedIn: false));
        service.Update(solo);

        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public void ToastFailureIsCaughtAndLoggedOnce()
    {
        var warnings = new List<string>();
        var attempts = 0;
        var service = new SoloDutyLeaveNoticeService(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("toast unavailable");
            },
            warnings.Add);
        var solo = TestDutyContextFactory.Create(DutyCategory.Solo);

        service.Update(solo);
        service.Update(solo);

        Assert.Equal(1, attempts);
        Assert.Single(warnings);
    }
}
