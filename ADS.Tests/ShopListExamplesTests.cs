using System.Text.Json;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ShopListExamplesTests
{
    [Fact]
    public void RelicOrdersHaveStableIdsAndPreserveUpgradeEditsSelectionAndDeletions()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var store = new ShopListPresetStore(first.Path);
        var other = new ShopListPresetStore(second.Path);
        var orders = store.Presets.Where(preset => preset.Mode == ShopListMode.FillOrderOverMultipleRuns).ToArray();
        Assert.Equal(2, orders.Length);
        Assert.Equal(13, orders.Sum(preset => preset.Items.Count));
        Assert.Equal(60, orders.SelectMany(preset => preset.Items).Single(row => row.ItemId == 15840).RefillToAtLeast);
        Assert.All(orders, order =>
        {
            Assert.Equal(JsonSerializer.Serialize(order), JsonSerializer.Serialize(other.Get(order.PresetId)));
            Assert.Equal(new ShopCurrencyIdentity(ShopCurrencyKind.Tomestone, 28), order.Currency);
            Assert.All(order.Items, row => { Assert.False(row.Repeatable); Assert.Equal(ShopListOwnershipScope.InventoryOnly, row.OwnershipScope); });
        });
        Assert.True(store.Select(orders[0].PresetId, out _));
        Assert.True(store.RenameActive("Edited ARR", out _));
        Assert.True(store.UpdateItem(orders[0].Items[0].RowId, 2, 2, false, ShopListOwnershipScope.InventoryOnly, out _));
        store.Reload();
        Assert.Equal(orders[0].PresetId, store.ActivePresetId);
        Assert.Equal("Edited ARR", store.ActivePresetName);
        Assert.Equal(2, store.ActivePreset.Items[0].RefillToAtLeast);
        Assert.True(store.DeleteActive(out _));
        store.Reload();
        Assert.DoesNotContain(store.Presets, preset => preset.PresetId == orders[0].PresetId);

        // Simulate a pre-feature store with an unrelated matching display name.
        var original = other.Presets.First();
        original.Name = "ARR Zodiac — Poetics";
        File.WriteAllText(other.ConfigPath, JsonSerializer.Serialize(new ShopListManifest
        { ActivePresetId = original.PresetId, Presets = [original] }));
        other.Reload();
        Assert.Equal(original.PresetId, other.ActivePresetId);
        Assert.Equal("ARR Zodiac — Poetics", other.ActivePresetName);
        Assert.Equal("ARR Zodiac — Poetics (2)", other.Get(orders[0].PresetId).Name);
        other.Reload();
        Assert.Equal(4, other.Presets.Count);
    }

    [Fact]
    public void FailedInitializationAndMalformedLoadsNeverReplaceExistingStore()
    {
        using var temp = new TempDirectory();
        var store = new ShopListPresetStore(temp.Path);
        Assert.True(store.Create("Keep me", out _));
        var active = store.ActivePresetId;
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.ConfigPath))!;
        document["RelicOrdersInitialized"] = false;
        File.WriteAllText(store.ConfigPath, document.ToJsonString());
        var before = File.ReadAllText(store.ConfigPath);
        using (var locked = new FileStream(store.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Reload();
            Assert.Contains("failed", store.LastStatus);
            Assert.Equal(active, store.ActivePresetId);
            Assert.Equal(before, File.ReadAllText(store.ConfigPath));
            Assert.False(store.Create("Cannot save", out _));
        }
        store.Reload();
        Assert.Equal(active, store.ActivePresetId);
        File.WriteAllText(store.ConfigPath, "{bad");
        store.Reload();
        Assert.Equal(active, store.ActivePresetId);
        Assert.Equal("{bad", File.ReadAllText(store.ConfigPath));
    }

    [Fact]
    public void BundledRelicExamplesPersistEditableCopiesWithoutReplacingExistingPresets()
    {
        using var temp = new TempDirectory();
        var store = new ShopListPresetStore(temp.Path);
        Assert.Equal(3, store.Presets.Count); // Two actual orders are automatic; step examples stay opt-in.
        Assert.True(store.Create("My shopping", out _));
        Assert.True(store.SetItem(42, 2, 4, true, ShopListOwnershipScope.InventoryAndRetainers, out _));
        var existingId = store.ActivePresetId;
        var existing = JsonSerializer.Serialize(store.ActivePreset);
        store.Reload();
        Assert.Equal(existingId, store.ActivePresetId);
        Assert.Equal(4, store.Presets.Count);

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
        Assert.Equal(14, store.Presets.Select(preset => preset.PresetId).Distinct().Count());

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
