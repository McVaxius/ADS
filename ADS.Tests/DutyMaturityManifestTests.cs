using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class DutyMaturityManifestTests
{
    [Fact]
    public void ChangedDropdownsAndNoteRoundTripThroughManifest()
    {
        var source = CreateDuty();
        source.ClearanceStatus = DutyClearanceStatus.OnePlayerUnsyncCleared;
        source.SupportLevel = DutySupportLevel.ActiveSupported;
        source.IsPlannedTest = true;
        source.SupportNote = "tried it, last map bit tricky, do later";

        var manifest = DutyCatalogService.BuildMaturityManifest([source]);

        var row = Assert.Single(manifest.Duties);
        Assert.Equal(source.ContentFinderConditionId, row.ContentFinderConditionId);
        Assert.Equal(source.TerritoryTypeId, row.TerritoryTypeId);
        Assert.Equal(source.EnglishName, row.DutyEnglishName);
        Assert.Equal(source.ClearanceStatus, row.ClearanceStatus);
        Assert.Equal(source.SupportLevel, row.SupportLevel);
        Assert.True(row.IsPlannedTest);
        Assert.Equal(source.SupportNote, row.SupportNote);

        var target = CreateDuty();
        var applied = DutyCatalogService.ApplyMaturityManifest([target], manifest);

        Assert.Equal(1, applied);
        Assert.Equal(source.ClearanceStatus, target.ClearanceStatus);
        Assert.Equal(source.SupportLevel, target.SupportLevel);
        Assert.True(target.IsPlannedTest);
        Assert.Equal(source.SupportNote, target.SupportNote);
    }

    [Fact]
    public void DefaultResetRowIsRemovedFromOverrides()
    {
        var source = CreateDuty();
        source.ClearanceStatus = DutyClearanceStatus.NotCleared;
        source.SupportLevel = DutySupportLevel.PassiveOnly;
        source.IsPlannedTest = false;
        source.SupportNote = DutyCatalogService.DefaultSupportNote;

        var manifest = DutyCatalogService.BuildMaturityManifest([source]);

        Assert.Empty(manifest.Duties);
    }

    private static DutyCatalogEntry CreateDuty()
        => new()
        {
            ContentFinderConditionId = 101,
            TerritoryTypeId = 202,
            Name = "Test Duty",
            EnglishName = "Test Duty",
            ContentTypeName = "Dungeon",
            ExpansionName = "Test",
            SupportNote = DutyCatalogService.DefaultSupportNote,
            LevelRequired = 1,
            SortKey = 1,
            ExVersion = 1,
            ContentTypeRowId = 1,
            ContentMemberTypeRowId = 4,
            PartySize = 4,
            Category = DutyCategory.FourMan,
            SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
        };
}
