using System.Text.Json;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ShopListExamplesTests
{
    [Fact]
    public void BundledRelicExamplesPersistEditableCopiesWithoutReplacingExistingPresets()
    {
        using var temp = new TempDirectory();
        var store = new ShopListPresetStore(temp.Path);
        Assert.Single(store.Presets); // Examples are opt-in, including on reload.
        Assert.True(store.Create("My shopping", out _));
        Assert.True(store.SetItem(42, 2, 4, true, ShopListOwnershipScope.InventoryAndRetainers, out _));
        var existingId = store.ActivePresetId;
        var existing = JsonSerializer.Serialize(store.ActivePreset);
        store.Reload();
        Assert.Equal(existingId, store.ActivePresetId);
        Assert.Equal(2, store.Presets.Count);

        var examples = ShopListExamples.RelicSteps;
        Assert.Equal(10, examples.Count);
        var expected = new Dictionary<uint, int>
        {
            [6267] = 1, [6268] = 3, [7885] = 3, [9540] = 4,
            [13582] = 10, [13584] = 10, [13586] = 10, [13588] = 10,
            [14899] = 5, [15840] = 20, [16064] = 50, [16933] = 15, [16934] = 1,
        };
        var allRows = new List<ShopListItem>();
        foreach (var example in examples)
        {
            Assert.True(store.AddExample(example, out var error), error);
            var preset = store.ActivePreset;
            Assert.Equal(example.Name, preset.Name);
            Assert.Equal(ShopListMode.TargetedRefill, preset.Mode);
            Assert.Equal(new ShopCurrencyIdentity(ShopCurrencyKind.Tomestone, 28), preset.Currency);
            Assert.Equal(0, preset.CurrencyThreshold);
            Assert.All(preset.Items, row =>
            {
                Assert.Equal(expected[row.ItemId], row.TriggerBelow);
                Assert.Equal(expected[row.ItemId], row.RefillToAtLeast);
                Assert.Equal(ShopListOwnershipScope.InventoryOnly, row.OwnershipScope);
                Assert.False(row.Repeatable);
                Assert.NotEqual(Guid.Empty, row.RowId);
            });
            allRows.AddRange(preset.Items);

            var saved = File.ReadAllText(store.ConfigPath);
            Assert.False(store.AddExample(example, out error));
            Assert.Contains("already exists", error);
            Assert.Equal(saved, File.ReadAllText(store.ConfigPath));
            store.Reload();
            Assert.Equal(JsonSerializer.Serialize(preset), JsonSerializer.Serialize(store.ActivePreset));
            Assert.Equal(existing, JsonSerializer.Serialize(store.Get(existingId)));
        }
        Assert.Equal(expected.Keys.Order(), allRows.Select(row => row.ItemId).Order());
        Assert.Equal(13, allRows.Select(row => row.RowId).Distinct().Count());
        Assert.DoesNotContain(allRows, row => row.ItemId == 7884);
        Assert.Equal(12, store.Presets.Select(preset => preset.PresetId).Distinct().Count());

        // Editing a saved copy must not change the bundled template or another preset.
        var original = store.ActivePreset;
        Assert.True(store.RenameActive("My adjusted relic", out _));
        Assert.True(store.UpdateItem(original.Items.Single().RowId, 2, 2, false,
            ShopListOwnershipScope.InventoryOnly, out _));
        Assert.True(store.AddExample(examples[^1], out _));
        Assert.NotEqual(original.PresetId, store.ActivePresetId);
        Assert.NotEqual(original.Items.Single().RowId, store.ActivePreset.Items.Single().RowId);
        Assert.Equal(1, store.ActivePreset.Items.Single().RefillToAtLeast);
        Assert.Equal(2, store.Get(original.PresetId).Items.Single().RefillToAtLeast);
    }
}
