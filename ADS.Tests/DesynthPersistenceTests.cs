using ADS.Services;

namespace ADS.Tests;

public sealed class DesynthPersistenceTests
{
    [Fact]
    public void PresetImportNormalizesAndRejectsDuplicateNamesAtomically()
    {
        using var temp = new TempDirectory();
        var store = new DesynthPresetStore(temp.Path);
        var valid = """{"Version":1,"Presets":[{"Name":"DEFAULT","Description":"","ItemIds":[1001234,1234,1234]}]}""";
        Assert.True(store.ImportRaw(valid, out _));
        Assert.Equal([(uint)1234], store.Get("DEFAULT").ItemIds);
        var before = store.ExportRaw();

        var invalid = """{"Version":1,"Presets":[{"Name":"DEFAULT","ItemIds":[]},{"Name":"default","ItemIds":[42]}]}""";
        Assert.False(store.ImportRaw(invalid, out _));
        Assert.Equal(before, store.ExportRaw());
    }

    [Fact]
    public void PresetBase64RoundTrips()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var source = new DesynthPresetStore(first.Path);
        Assert.True(source.Create("Raid", "drops", out _));
        Assert.True(source.AddItem("Raid", 77, out _));

        var target = new DesynthPresetStore(second.Path);
        Assert.True(target.ImportBase64(source.ExportBase64(), out _));
        Assert.Equal([(uint)77], target.Get("Raid").ItemIds);
    }

    [Fact]
    public void PresetClipboardImportAcceptsJsonAndLegacyBase64AndPreservesDescription()
    {
        using var sourceDirectory = new TempDirectory();
        var source = new DesynthPresetStore(sourceDirectory.Path);
        Assert.True(source.Create("Raid", "legacy description", out _));
        Assert.True(source.AddItem("Raid", 77, out _));

        using var jsonDirectory = new TempDirectory();
        var jsonTarget = new DesynthPresetStore(jsonDirectory.Path);
        Assert.True(jsonTarget.ImportClipboard(source.ExportRaw(), out _));
        Assert.Equal("legacy description", jsonTarget.Get("Raid").Description);
        Assert.Equal([(uint)77], jsonTarget.Get("Raid").ItemIds);

        using var base64Directory = new TempDirectory();
        var base64Target = new DesynthPresetStore(base64Directory.Path);
        Assert.True(base64Target.ImportClipboard(source.ExportBase64(), out _));
        base64Target.Reload();
        Assert.Equal("legacy description", base64Target.Get("Raid").Description);
        Assert.Equal([(uint)77], base64Target.Get("Raid").ItemIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON or base64")]
    public void InvalidClipboardImportLeavesPresetsUnchanged(string clipboard)
    {
        using var temp = new TempDirectory();
        var store = new DesynthPresetStore(temp.Path);
        Assert.True(store.Create("Keep", "unchanged", out _));
        Assert.True(store.AddItem("Keep", 42, out _));
        var before = store.ExportRaw();

        Assert.False(store.ImportClipboard(clipboard, out var error));
        Assert.NotEmpty(error);
        Assert.Equal(before, store.ExportRaw());
    }

    [Fact]
    public void PresetItemAddAndRemovePersistAcrossReload()
    {
        using var temp = new TempDirectory();
        var store = new DesynthPresetStore(temp.Path);
        Assert.True(store.Create("Keep", string.Empty, out _));
        Assert.True(store.AddItem("Keep", 1001234, out _));

        store.Reload();
        Assert.Equal([(uint)1234], store.Get("Keep").ItemIds);

        Assert.True(store.RemoveItem("Keep", 1001234, out _));
        store.Reload();
        Assert.Empty(store.Get("Keep").ItemIds);
    }

    [Fact]
    public void LedgerFinalizesPositiveCompletedDutyDeltasAndConsumes()
    {
        using var temp = new TempDirectory();
        var store = new DesynthDutyLedgerStore(temp.Path);
        var counts = new Dictionary<uint, int> { [1001234] = 2 };
        store.Update(false, 0, true, () => new Dictionary<uint, int>(counts));
        store.Update(true, 777, true, () => new Dictionary<uint, int>(counts));
        counts[1001234] = 5;
        store.MarkDutyCompleted();
        store.Update(false, 777, true, () => new Dictionary<uint, int>(counts));

        Assert.Equal(3, store.GetRemainingCounts()[1234]);
        Assert.True(store.Consume(1001234));
        Assert.Equal(2, store.GetRemainingCounts()[1234]);
    }

    [Fact]
    public void LedgerPreservesAbandonedDutyUntilNextTrackedDutyBegins()
    {
        using var temp = new TempDirectory();
        var store = new DesynthDutyLedgerStore(temp.Path);
        store.Update(false, 0, true, () => []);
        store.Update(true, 1, true, () => []);
        store.Update(false, 1, true, () => []);
        Assert.True(store.Ledger?.Abandoned);

        store.Update(true, 2, true, () => []);
        Assert.Null(store.Ledger);
        Assert.Equal((uint)2, store.Active?.TerritoryTypeId);
    }

    [Theory]
    [InlineData("utility.start-desynth", true)]
    [InlineData("utility.start-extract-materia", true)]
    [InlineData("configuration.patch", true)]
    [InlineData("unknown.action", false)]
    [InlineData("", false)]
    public void IpcActionValidationRejectsUnknownActions(string action, bool expected)
        => Assert.Equal(expected, AdsIpcValidation.IsKnownAction(action));

    [Theory]
    [InlineData("desynthInventoryScope", true)]
    [InlineData("desynthSource", true)]
    [InlineData("desynthCategories", true)]
    [InlineData("lootMode", true)]
    [InlineData("lootRegistrableNeedingEnabled", true)]
    [InlineData("lootRegistrableMountsEnabled", true)]
    [InlineData("lootRegistrableMinionsEnabled", true)]
    [InlineData("lootRegistrableFashionAccessoriesEnabled", true)]
    [InlineData("lootRegistrableFacewearEnabled", true)]
    [InlineData("lootRegistrableOrchestrionRollsEnabled", true)]
    [InlineData("lootRegistrableFadedOrchestrionCopiesEnabled", true)]
    [InlineData("lootRegistrableEmotesHairstylesEnabled", true)]
    [InlineData("lootRegistrableBardingsEnabled", true)]
    [InlineData("lootRegistrableTripleTriadCardsEnabled", true)]
    [InlineData("releaseVersion", false)]
    public void IpcConfigurationValidationRejectsUnknownSettings(string setting, bool expected)
        => Assert.Equal(expected, AdsIpcValidation.IsKnownConfigurationSetting(setting));
}
