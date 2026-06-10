using ADS;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class DesynthPolicyTests
{
    [Fact]
    public void ConfiguredPolicyComposesConfiguredSourceAndFilters()
    {
        using var temp = new TempDirectory();
        var presets = new DesynthPresetStore(temp.Path);
        Assert.True(presets.AddItem("DEFAULT", 1_001_234, out _));
        var ledger = new DesynthDutyLedgerStore(temp.Path);
        var config = new Configuration
        {
            DesynthSource = DesynthSource.ActiveWhitelist,
            DesynthInventoryScope = DesynthInventoryScope.InventoryOnly,
            DesynthSkillUpFilterEnabled = true,
            DesynthSkillUpThreshold = 50,
        };

        var policy = new DesynthPolicyService().Compose(DesynthRunMode.Configured, config, presets, ledger);

        Assert.Equal(DesynthSource.ActiveWhitelist, policy.Source);
        Assert.Equal(DesynthInventoryScope.InventoryOnly, policy.Scope);
        Assert.True(policy.SkillUpFilterEnabled);
        Assert.True(policy.ProtectGearsets);
        Assert.Contains((uint)1234, policy.Whitelist);
        Assert.Equal(["InventoryEquipment"], policy.Categories);
    }

    [Theory]
    [InlineData(99f, 50u, 50, 100f, true)]
    [InlineData(100f, 50u, 50, 100f, false)]
    [InlineData(99f, 49u, 50, 100f, false)]
    [InlineData(0f, 50u, 50, 100f, false)]
    public void SkillUpBoundariesMatchAutoDuty(float level, uint itemLevel, int threshold, float maximum, bool expected)
    {
        var policy = new DesynthPolicy(
            DesynthRunMode.Skillups,
            DesynthSource.AllInventory,
            DesynthInventoryScope.InventoryOnly,
            "DEFAULT",
            true,
            threshold,
            true,
            new HashSet<string> { "InventoryEquipment" },
            new HashSet<uint>(),
            new Dictionary<uint, int>());

        var candidate = new DesynthCandidate(1234, "InventoryEquipment", itemLevel, level, maximum, false);
        Assert.Equal(expected, policy.IsEligible(candidate));
    }

    [Fact]
    public void SkillupsOverridesSourceCategoriesAndGearsetProtection()
    {
        using var temp = new TempDirectory();
        var presets = new DesynthPresetStore(temp.Path);
        var ledger = new DesynthDutyLedgerStore(temp.Path);
        var config = new Configuration
        {
            DesynthSource = DesynthSource.ActiveWhitelist,
            DesynthInventoryScope = DesynthInventoryScope.InventoryAndArmourySkipGearsets,
            DesynthSkillUpFilterEnabled = false,
        };

        var policy = new DesynthPolicyService().Compose(DesynthRunMode.Skillups, config, presets, ledger);

        Assert.Equal(DesynthSource.AllInventory, policy.Source);
        Assert.Equal(DesynthInventoryScope.InventoryAndArmourySkipGearsets, policy.Scope);
        Assert.True(policy.SkillUpFilterEnabled);
        Assert.True(policy.ProtectGearsets);
        Assert.Contains("ArmouryMainOff", policy.Categories);
        Assert.DoesNotContain("InventoryHousing", policy.Categories);
    }

    [Theory]
    [InlineData(DesynthRunMode.Configured, DesynthSource.LastDutyGains)]
    [InlineData(DesynthRunMode.All, DesynthSource.AllInventory)]
    [InlineData(DesynthRunMode.Whitelist, DesynthSource.ActiveWhitelist)]
    [InlineData(DesynthRunMode.LastDuty, DesynthSource.LastDutyGains)]
    public void RunModesComposeExpectedSource(DesynthRunMode mode, DesynthSource expected)
    {
        using var temp = new TempDirectory();
        var presets = new DesynthPresetStore(temp.Path);
        var ledger = new DesynthDutyLedgerStore(temp.Path);
        var config = new Configuration
        {
            DesynthSource = DesynthSource.LastDutyGains,
            DesynthInventoryScope = DesynthInventoryScope.InventoryOnly,
        };

        var policy = new DesynthPolicyService().Compose(mode, config, presets, ledger);

        Assert.Equal(expected, policy.Source);
    }

    [Fact]
    public void DefaultConfigMapsToInventoryOnly()
    {
        var config = new Configuration();
        DesynthPolicyService.ApplyScopeToConfiguration(config, config.DesynthInventoryScope);

        Assert.Equal(DesynthInventoryScope.InventoryOnly, config.DesynthInventoryScope);
        Assert.Equal(["InventoryEquipment"], config.DesynthCategories);
        Assert.True(config.DesynthProtectGearsets);
    }

    [Theory]
    [InlineData(true, DesynthInventoryScope.InventoryAndArmourySkipGearsets)]
    [InlineData(false, DesynthInventoryScope.InventoryAndArmoury)]
    public void LegacyArmouryCategoriesNormalizeToScope(bool protectGearsets, DesynthInventoryScope expected)
    {
        var scope = DesynthPolicyService.NormalizeScopeFromLegacyCategories(
            ["InventoryEquipment", "ArmouryMainOff"],
            protectGearsets);

        Assert.Equal(expected, scope);
    }

    [Fact]
    public void LegacyJunkCategoriesNormalizeToInventoryOnly()
    {
        var config = new Configuration
        {
            DesynthInventoryScope = DesynthPolicyService.NormalizeScopeFromLegacyCategories(
                ["InventoryHousing", "Equipped"],
                protectGearsets: false),
            DesynthProtectGearsets = false,
            DesynthCategories = ["InventoryHousing", "Equipped"],
        };

        DesynthPolicyService.ApplyScopeToConfiguration(config, config.DesynthInventoryScope);

        Assert.Equal(DesynthInventoryScope.InventoryOnly, config.DesynthInventoryScope);
        Assert.Equal(["InventoryEquipment"], config.DesynthCategories);
    }

    [Fact]
    public void InventoryOnlyOnlyPermitsInventoryEquipment()
    {
        var policy = ComposeScope(DesynthInventoryScope.InventoryOnly);

        Assert.True(policy.IsEligible(new DesynthCandidate(1234, "InventoryEquipment", 1, 1, 100, false)));
        Assert.False(policy.IsEligible(new DesynthCandidate(1234, "ArmouryMainOff", 1, 1, 100, false)));
    }

    [Fact]
    public void EverywhereSkipGearsetsIncludesArmouryAndExcludesGearsetItems()
    {
        var policy = ComposeScope(DesynthInventoryScope.InventoryAndArmourySkipGearsets);

        Assert.True(policy.IsEligible(new DesynthCandidate(1234, "ArmouryMainOff", 1, 1, 100, false)));
        Assert.False(policy.IsEligible(new DesynthCandidate(1234, "ArmouryMainOff", 1, 1, 100, true)));
    }

    [Fact]
    public void EverywhereIncludesArmouryAndAllowsGearsetItems()
    {
        var policy = ComposeScope(DesynthInventoryScope.InventoryAndArmoury);

        Assert.True(policy.IsEligible(new DesynthCandidate(1234, "ArmouryMainOff", 1, 1, 100, true)));
    }

    [Theory]
    [InlineData("configured", DesynthRunMode.Configured)]
    [InlineData("all", DesynthRunMode.All)]
    [InlineData("whitelist", DesynthRunMode.Whitelist)]
    [InlineData("last-duty", DesynthRunMode.LastDuty)]
    [InlineData("skillups", DesynthRunMode.Skillups)]
    [InlineData("inventory-only", DesynthRunMode.InventoryOnly)]
    [InlineData("everywhere-skip-gearsets", DesynthRunMode.EverywhereSkipGearsets)]
    [InlineData("everywhere", DesynthRunMode.Everywhere)]
    public void RunModeAliasesParse(string value, DesynthRunMode expected)
    {
        Assert.True(DesynthPolicyService.TryParseMode(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void OperatorLegacyCategoriesNormalizeToScope()
    {
        Assert.True(DesynthPolicyService.TryNormalizeLegacyCategories(
            ["InventoryEquipment", "ArmouryWristsRings"],
            protectGearsets: true,
            out var scope,
            out var error));

        Assert.Equal(DesynthInventoryScope.InventoryAndArmourySkipGearsets, scope);
        Assert.Empty(error);
    }

    private static DesynthPolicy ComposeScope(DesynthInventoryScope scope)
        => new(
            DesynthRunMode.Configured,
            DesynthSource.AllInventory,
            scope,
            "DEFAULT",
            false,
            50,
            DesynthPolicyService.ScopeProtectsGearsets(scope),
            DesynthPolicyService.GetCategoryNames(scope).ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<uint>(),
            new Dictionary<uint, int>());
}
