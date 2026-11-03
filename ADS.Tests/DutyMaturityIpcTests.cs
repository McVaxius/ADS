using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Xunit;

namespace ADS.Tests;

public sealed class DutyMaturityIpcTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void ExactDutyReportsLiveMaturityWithoutChangingTheCatalog(int level, bool eligible)
    {
        var entry = Entry((DutyClearanceStatus)level);
        using var json = JsonDocument.Parse(DutyMaturityIpc.Query([entry], 901, 41));
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(901u, root.GetProperty("territoryTypeId").GetUInt32());
        Assert.Equal(41u, root.GetProperty("contentFinderConditionId").GetUInt32());
        Assert.Equal(level, root.GetProperty("clearanceLevel").GetInt32());
        Assert.Equal(((DutyClearanceStatus)level).ToString(), root.GetProperty("clearanceStatus").GetString());
        Assert.Equal(eligible, root.GetProperty("eligibleForQuest").GetBoolean());
        Assert.Equal((DutyClearanceStatus)level, entry.ClearanceStatus);
        Assert.Equal(DutySupportLevel.PassiveOnly, entry.SupportLevel);
    }

    [Theory]
    [InlineData(0u, 41u)]
    [InlineData(901u, 0u)]
    [InlineData(902u, 41u)]
    [InlineData(901u, 42u)]
    public void MissingOrMismatchedIdentityCannotBorrowAnotherDutyMaturity(uint territory, uint cfc)
    {
        using var json = JsonDocument.Parse(DutyMaturityIpc.Query([Entry(DutyClearanceStatus.FourPlayerSyncCleared)], territory, cfc));
        AssertUnavailable(json.RootElement);
    }

    [Fact]
    public void UnknownDuplicateAndInvalidRowsRemainUnavailable()
    {
        var valid = Entry(DutyClearanceStatus.OnePlayerDutySupport);
        foreach (var catalog in new[] { Array.Empty<DutyCatalogEntry>(), new[] { valid, valid }, new[] { Entry((DutyClearanceStatus)4) } })
        {
            using var json = JsonDocument.Parse(DutyMaturityIpc.Query(catalog, 901, 41));
            AssertUnavailable(json.RootElement);
        }
    }

    [Fact]
    public void QueryReflectsMaturityEditsWithoutReloadOrCachedState()
    {
        var entry = Entry(DutyClearanceStatus.NotCleared);
        using var before = JsonDocument.Parse(DutyMaturityIpc.Query([entry], 901, 41));
        Assert.False(before.RootElement.GetProperty("eligibleForQuest").GetBoolean());
        entry.ClearanceStatus = DutyClearanceStatus.OnePlayerUnsyncCleared;
        using var after = JsonDocument.Parse(DutyMaturityIpc.Query([entry], 901, 41));
        Assert.True(after.RootElement.GetProperty("eligibleForQuest").GetBoolean());
    }

    private static void AssertUnavailable(JsonElement root)
    {
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("eligibleForQuest").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("clearanceLevel").ValueKind);
        Assert.NotEmpty(root.GetProperty("message").GetString()!);
    }

    private static DutyCatalogEntry Entry(DutyClearanceStatus clearance) => new()
    {
        TerritoryTypeId = 901, ContentFinderConditionId = 41, Name = "Synthetic duty", EnglishName = "Synthetic duty",
        ContentTypeName = "Dungeon", ExpansionName = "Synthetic", SupportNote = "unchanged",
        LevelRequired = 1, SortKey = 1, ExVersion = 0, ContentTypeRowId = 2, ContentMemberTypeRowId = 1,
        PartySize = 4, Category = DutyCategory.FourMan, SupportLevel = DutySupportLevel.PassiveOnly,
        ClearanceStatus = clearance, IsPlannedTest = false, IsMainScenario = false,
    };
}
