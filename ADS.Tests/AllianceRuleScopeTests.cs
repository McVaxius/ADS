using System.Reflection;
using System.Text;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;

namespace ADS.Tests;

public sealed class AllianceRuleScopeTests
{
    [Theory]
    [InlineData(true, 8, false, 0, "A")]
    [InlineData(true, 8, false, 1, "B")]
    [InlineData(true, 8, false, 2, "C")]
    [InlineData(true, 4, true, 3, "D")]
    [InlineData(true, 4, true, 4, "E")]
    [InlineData(true, 4, true, 5, "F")]
    [InlineData(true, 8, false, 3, null)]
    [InlineData(true, 4, true, 6, null)]
    [InlineData(true, 8, false, 255, null)]
    [InlineData(false, 8, false, 1, null)]
    [InlineData(true, 0, false, 1, null)]
    [InlineData(true, 9, false, 1, null)]
    public unsafe void NativeAllianceUsesMappedLocalPartyWithoutVisibleAddon(
        bool isAlliance, byte members, bool smallGroups, byte index, string? expected)
    {
        GroupManager manager = default;
        manager.MainGroup.IsAlliance = isAlliance;
        manager.MainGroup.IsSmallGroupAlliance = smallGroups;
        manager.MainGroup.MemberCount = members;
        manager.MainGroup.AllianceGroupIndices[0] = index;
        var party = DispatchProxy.Create<IPartyList, NativePartyProxy>();
        ((NativePartyProxy)(object)party).Address = (nint)(&manager);
        var service = new DutyContextService(null!, null!, null!, party);

        Assert.Equal(expected, service.TryResolveAlliance());
    }

    [Fact]
    public void MissingNativeGroupManagerLeavesAllianceUnknown()
    {
        var party = DispatchProxy.Create<IPartyList, NativePartyProxy>();
        var service = new DutyContextService(null!, null!, null!, party);

        Assert.Null(service.TryResolveAlliance());
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("b", "B")]
    [InlineData(" C ", "C")]
    [InlineData("Alliance: D", "D")]
    [InlineData("e", "E")]
    [InlineData("Alliance: F", "F")]
    [InlineData("Alliance: G", "G")]
    [InlineData("Alliance: A", "A")]
    [InlineData("アライアンス: B", "B")]
    [InlineData("Équipe — C", "C")]
    public void ParserResolvesOneStandaloneAllianceLabel(string text, string expected)
        => Assert.Equal(expected, AllianceScopeParser.Parse(isAlliance: true, text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alliance")]
    [InlineData("Alliance H")]
    [InlineData("Alliance AB")]
    [InlineData("A / B")]
    [InlineData("D / E")]
    public void ParserRejectsBlankOrMalformedLabels(string? text)
        => Assert.Null(AllianceScopeParser.Parse(isAlliance: true, text));

    [Theory]
    [InlineData("A")]
    [InlineData("b")]
    [InlineData(" C ")]
    [InlineData("d")]
    [InlineData("E")]
    [InlineData(" f ")]
    [InlineData("G")]
    public void AllianceScopeValidationAcceptsAThroughG(string alliance)
        => Assert.True(AllianceScopeParser.IsValidScope(alliance));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("H")]
    [InlineData("AB")]
    [InlineData("A/B")]
    [InlineData("Alliance: D")]
    public void AllianceScopeValidationRejectsMalformedLabels(string? alliance)
        => Assert.False(AllianceScopeParser.IsValidScope(alliance));

    [Fact]
    public void ParserRejectsPartyTextOutsideAllianceState()
        => Assert.Null(AllianceScopeParser.Parse(isAlliance: false, "Alliance: A"));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData(" \t ", null, true)]
    [InlineData(null, "B", true)]
    [InlineData("A", "A", true)]
    [InlineData("a", "A", true)]
    [InlineData("A", "B", false)]
    [InlineData("A", null, false)]
    [InlineData("D", "D", true)]
    [InlineData("E", "E", true)]
    [InlineData("F", "F", true)]
    [InlineData("G", "G", true)]
    [InlineData("D", "G", false)]
    [InlineData("D", null, false)]
    [InlineData("H", "D", false)]
    public void OrdinaryObjectRulesHonorAllianceScope(string? ruleAlliance, string? liveAlliance, bool expected)
    {
        using var fixture = new RuleServiceFixture(
            new ObjectPriorityRule
            {
                ObjectKind = ObjectKind.EventObj.ToString(),
                ObjectName = "Scoped Door",
                NameMatchMode = "Exact",
                Classification = nameof(InteractableClass.Required),
            });

        // Exercise malformed runtime data without persisting an invalid scope.
        fixture.Service.Current.Rules[0].Alliance = ruleAlliance;

        var match = fixture.Service.MatchObjectRule(
            TestDutyContextFactory.Create(alliance: liveAlliance),
            ObjectKind.EventObj,
            baseId: 0,
            objectName: "Scoped Door");

        Assert.Equal(expected, match is not null);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData(" \t ", null, true)]
    [InlineData(null, "C", true)]
    [InlineData("C", "C", true)]
    [InlineData("C", "A", false)]
    [InlineData("C", null, false)]
    [InlineData("D", "D", true)]
    [InlineData("E", "E", true)]
    [InlineData("F", "F", true)]
    [InlineData("G", "G", true)]
    [InlineData("D", "G", false)]
    [InlineData("G", null, false)]
    [InlineData("invalid", "C", false)]
    public void ManualDestinationRulesHonorAllianceScope(string? ruleAlliance, string? liveAlliance, bool expected)
    {
        using var fixture = new RuleServiceFixture(
            new ObjectPriorityRule
            {
                Classification = nameof(InteractableClass.MapXzDestination),
                MapCoordinates = "11.3,10.4",
            });

        fixture.Service.Current.Rules[0].Alliance = ruleAlliance;

        var matches = fixture.Service.GetMapXzDestinationRules(
            TestDutyContextFactory.Create(alliance: liveAlliance));

        Assert.Equal(expected, matches.Count == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameObjectAllianceScopeOverridesWildcardDespitePriorityAndInsertionOrder(bool scopedFirst)
    {
        var wildcard = new ObjectPriorityRule
        {
            ObjectKind = ObjectKind.EventObj.ToString(),
            ObjectName = "Scoped Door",
            Classification = nameof(InteractableClass.Ignored),
            Priority = -1000,
        };
        var scoped = ObjectRuleShardStore.CloneRule(wildcard);
        scoped.Alliance = "A";
        scoped.Classification = nameof(InteractableClass.Required);
        scoped.Priority = 1000;
        using var fixture = new RuleServiceFixture(scopedFirst ? [scoped, wildcard] : [wildcard, scoped]);

        var match = fixture.Service.MatchObjectRule(
            TestDutyContextFactory.Create(alliance: "A"),
            ObjectKind.EventObj,
            baseId: 0,
            objectName: "Scoped Door");

        Assert.Equal(nameof(InteractableClass.Required), match?.Classification);
        Assert.Equal("A", match?.Alliance);
    }

    [Fact]
    public void AllianceScopeDoesNotOverrideMoreSpecificObjectDutyScope()
    {
        var territorial = new ObjectPriorityRule
        {
            TerritoryTypeId = 777,
            ObjectKind = ObjectKind.EventObj.ToString(),
            ObjectName = "Scoped Door",
            Classification = nameof(InteractableClass.Ignored),
            Priority = 1000,
        };
        var scoped = ObjectRuleShardStore.CloneRule(territorial);
        scoped.TerritoryTypeId = 0;
        scoped.Alliance = "A";
        scoped.Classification = nameof(InteractableClass.Required);
        scoped.Priority = -1000;
        using var fixture = new RuleServiceFixture(scoped, territorial);

        var match = fixture.Service.MatchObjectRule(
            TestDutyContextFactory.Create(alliance: "A"),
            ObjectKind.EventObj,
            baseId: 0,
            objectName: "Scoped Door");

        Assert.Equal(nameof(InteractableClass.Ignored), match?.Classification);
        Assert.Equal(777u, match?.TerritoryTypeId);
        Assert.Null(match?.Alliance);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManualDestinationAllianceScopeOverridesWildcardDespitePriorityAndInsertionOrder(
        bool xyz, bool scopedFirst)
    {
        var wildcard = new ObjectPriorityRule
        {
            Classification = xyz ? nameof(InteractableClass.XYZ) : nameof(InteractableClass.MapXzDestination),
            MapCoordinates = "11.3,10.4",
            WorldCoordinates = "10,20,30",
            Priority = -1000,
        };
        var scoped = ObjectRuleShardStore.CloneRule(wildcard);
        scoped.Alliance = "A";
        scoped.Priority = 1000;
        using var fixture = new RuleServiceFixture(scopedFirst ? [scoped, wildcard] : [wildcard, scoped]);
        var context = TestDutyContextFactory.Create(alliance: "A");

        var matches = xyz
            ? fixture.Service.GetXyzDestinationRules(context)
            : fixture.Service.GetMapXzDestinationRules(context);

        Assert.Equal(2, matches.Count);
        Assert.Equal("A", matches[0].Alliance);
        Assert.Null(matches[1].Alliance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualDestinationAllianceScopeDoesNotOverrideMoreSpecificDutyScope(bool xyz)
    {
        var territorial = new ObjectPriorityRule
        {
            TerritoryTypeId = 777,
            Classification = xyz ? nameof(InteractableClass.XYZ) : nameof(InteractableClass.MapXzDestination),
            MapCoordinates = "11.3,10.4",
            WorldCoordinates = "10,20,30",
            Priority = 1000,
        };
        var scoped = ObjectRuleShardStore.CloneRule(territorial);
        scoped.TerritoryTypeId = 0;
        scoped.Alliance = "A";
        scoped.Priority = -1000;
        using var fixture = new RuleServiceFixture(scoped, territorial);
        var context = TestDutyContextFactory.Create(alliance: "A");

        var matches = xyz
            ? fixture.Service.GetXyzDestinationRules(context)
            : fixture.Service.GetMapXzDestinationRules(context);

        Assert.Equal(2, matches.Count);
        Assert.Equal(777u, matches[0].TerritoryTypeId);
        Assert.Null(matches[0].Alliance);
        Assert.Equal("A", matches[1].Alliance);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("H")]
    [InlineData(" AB ")]
    [InlineData("A/B")]
    [InlineData("Alliance: D")]
    public void ManifestImportsRejectNonblankInvalidAlliance(string alliance)
    {
        using var fixture = new RuleServiceFixture(new ObjectPriorityRule { ObjectName = "Retained" });
        var json = JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Rules = [new ObjectPriorityRule(), new ObjectPriorityRule { Alliance = alliance }],
        });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        foreach (var payload in new[] { json, base64 })
        {
            Assert.False(fixture.Service.TryImportManifestText(payload, out _, out var status));
            Assert.Contains("Alliance", status, StringComparison.OrdinalIgnoreCase);
            Assert.False(fixture.Service.TryImportRulesText(payload, out _, out _, out status));
            Assert.Contains("Alliance", status, StringComparison.OrdinalIgnoreCase);
        }

        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "invalid-alliance.json");
        File.WriteAllText(path, json);
        Assert.False(fixture.Service.TryImportManifestFromPath(path, out _, out var diskStatus));
        Assert.Contains("Alliance", diskStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Retained", Assert.Single(fixture.Service.Current.Rules).ObjectName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAllianceIsRejectedByShardsSavesAndPresetTransfers(bool territoryScoped)
    {
        using var fixture = new RuleServiceFixture(new ObjectPriorityRule { ObjectName = "Retained" });
        var persistedPath = fixture.Service.GetContextShardPath(
            ObjectPriorityRuleService.DefaultPresetName, ObjectRuleShardStore.GlobalFileName);
        var persistedJson = File.ReadAllText(persistedPath);
        var manifest = new ObjectPriorityRuleManifest
        {
            Rules = [new ObjectPriorityRule { TerritoryTypeId = territoryScoped ? 777u : 0, Alliance = "H" }],
        };
        var fileName = territoryScoped
            ? ObjectRuleShardStore.GetTerritoryFileName(777)
            : ObjectRuleShardStore.GlobalFileName;
        var json = JsonSerializer.Serialize(manifest);

        Assert.False(ObjectRuleShardStore.TryValidateShardJson(json, fileName, [], out _, out var status));
        Assert.Contains("Alliance", status, StringComparison.OrdinalIgnoreCase);
        Assert.False(ObjectRuleShardStore.TrySplitManifest(manifest, [], out _, out status));
        Assert.Contains("Alliance", status, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Service.SaveManifest(manifest));
        Assert.Contains("Alliance", fixture.Service.LastLoadStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(persistedJson, File.ReadAllText(persistedPath));
        Assert.Equal("Retained", Assert.Single(fixture.Service.Current.Rules).ObjectName);

        var transfer = new ObjectRulePresetTransfer
        {
            PresetName = "Scoped import",
            Contexts = new Dictionary<string, ObjectPriorityRuleManifest> { [fileName] = manifest },
        };
        var transferJson = JsonSerializer.Serialize(transfer);
        foreach (var payload in new[] { transferJson, Convert.ToBase64String(Encoding.UTF8.GetBytes(transferJson)) })
        {
            Assert.False(fixture.Service.TryImportRulesText(payload, out _, out _, out status));
            Assert.Contains("Alliance", status, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void InvalidAllianceOnDiskRejectsReloadAndRetainsCurrentRules()
    {
        using var fixture = new RuleServiceFixture(new ObjectPriorityRule { ObjectName = "Retained" });
        var path = fixture.Service.GetContextShardPath(
            ObjectPriorityRuleService.DefaultPresetName, ObjectRuleShardStore.GlobalFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new ObjectPriorityRuleManifest
        {
            Rules = [new ObjectPriorityRule { Alliance = "H", ObjectName = "Rejected" }],
        }));

        Assert.False(fixture.Service.Reload());
        Assert.Contains("Alliance", fixture.Service.LastLoadStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Retained", Assert.Single(fixture.Service.Current.Rules).ObjectName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void NullOrBlankAllianceRemainsWildcardThroughImportsAndShards(string? alliance)
    {
        using var fixture = new RuleServiceFixture(new ObjectPriorityRule
        {
            Alliance = alliance,
            ObjectName = "Wildcard Door",
            Classification = nameof(InteractableClass.Required),
        });
        var path = fixture.Service.GetContextShardPath(
            ObjectPriorityRuleService.DefaultPresetName, ObjectRuleShardStore.GlobalFileName);
        var json = File.ReadAllText(path);

        Assert.True(ObjectRuleShardStore.TryValidateShardJson(
            json, ObjectRuleShardStore.GlobalFileName, [], out var shard, out var status), status);
        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(shard.Rules).Alliance));
        foreach (var payload in new[] { json, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) })
        {
            Assert.True(fixture.Service.TryImportManifestText(payload, out var imported, out status), status);
            Assert.True(string.IsNullOrWhiteSpace(Assert.Single(imported.Rules).Alliance));
        }
        Assert.True(fixture.Service.TryImportManifestFromPath(path, out var fromDisk, out status), status);
        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(fromDisk.Rules).Alliance));
        var transferJson = JsonSerializer.Serialize(new ObjectRulePresetTransfer
        {
            PresetName = "Wildcard import",
            Contexts = new Dictionary<string, ObjectPriorityRuleManifest>
            {
                [ObjectRuleShardStore.GlobalFileName] = shard,
            },
        });
        Assert.True(fixture.Service.TryImportRulesText(transferJson, out var transfer, out _, out status), status);
        Assert.NotNull(transfer);
        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(
            transfer.Contexts[ObjectRuleShardStore.GlobalFileName].Rules).Alliance));
        Assert.NotNull(fixture.Service.MatchObjectRule(
            TestDutyContextFactory.Create(alliance: null), ObjectKind.EventObj, 0, "Wildcard Door"));
    }

    [Fact]
    public void SaveReloadAndEditableCopyPreserveNullAndExplicitAlliance()
    {
        using var fixture = new RuleServiceFixture(
            new ObjectPriorityRule { Alliance = null, Classification = nameof(InteractableClass.Ignored) },
            new ObjectPriorityRule { Alliance = "B", Classification = nameof(InteractableClass.Ignored) });

        Assert.Equal(1, fixture.Service.Current.SchemaVersion);
        Assert.Null(fixture.Service.Current.Rules[0].Alliance);
        Assert.Equal("B", fixture.Service.Current.Rules[1].Alliance);

        var editable = fixture.Service.CreateEditableCopy();
        Assert.Null(editable.Rules[0].Alliance);
        Assert.Equal("B", editable.Rules[1].Alliance);
        Assert.NotSame(fixture.Service.Current.Rules[1], editable.Rules[1]);

        var persistedJson = File.ReadAllText(fixture.Service.GetContextShardPath(
            ObjectPriorityRuleService.DefaultPresetName,
            ObjectRuleShardStore.GlobalFileName));
        Assert.Contains("\"Alliance\": null", persistedJson, StringComparison.Ordinal);
        Assert.Contains("\"Alliance\": \"B\"", persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorAndClipboardCloningPreserveNullAndExplicitAlliance()
    {
        var manifest = new ObjectPriorityRuleManifest
        {
            Rules =
            [
                new ObjectPriorityRule { Alliance = null },
                new ObjectPriorityRule { Alliance = "C" },
            ],
        };

        var editorClone = ObjectRuleEditorWindow.CloneManifest(manifest);
        Assert.Null(editorClone.Rules[0].Alliance);
        Assert.Equal("C", editorClone.Rules[1].Alliance);

        var rowJson = JsonSerializer.Serialize(manifest.Rules[1]);
        var rowPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(rowJson));
        var clipboardClone = JsonSerializer.Deserialize<ObjectPriorityRule>(
            Encoding.UTF8.GetString(Convert.FromBase64String(rowPayload)));
        Assert.Equal("C", clipboardClone?.Alliance);

        using var fixture = new RuleServiceFixture();
        var manifestPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
        Assert.True(fixture.Service.TryImportManifestText(manifestPayload, out var imported, out _));
        Assert.Null(imported.Rules[0].Alliance);
        Assert.Equal("C", imported.Rules[1].Alliance);
    }

    [Fact]
    public void SchemaOneManifestWithoutAllianceDeserializesAsWildcard()
    {
        const string legacyManifest = """
            {
              "schemaVersion": 1,
              "rules": [
                {
                  "classification": "Ignored"
                }
              ]
            }
            """;

        using var fixture = new RuleServiceFixture();
        Assert.True(fixture.Service.TryImportManifestText(legacyManifest, out var imported, out _));
        Assert.Equal(1, imported.SchemaVersion);
        Assert.Null(Assert.Single(imported.Rules).Alliance);
    }

    [Fact]
    public void AllianceOnlyRowsAreScopedRatherThanGlobal()
    {
        Assert.True(ObjectRuleEditorWindow.IsGlobalAreaRule(new ObjectPriorityRule()));
        Assert.False(ObjectRuleEditorWindow.IsGlobalAreaRule(new ObjectPriorityRule { Alliance = "A" }));
    }

    [Fact]
    public void ScopeDescriptionIncludesAlliance()
    {
        using var fixture = new RuleServiceFixture();
        Assert.Equal(
            "Alliance A",
            fixture.Service.DescribeRuleScope(new ObjectPriorityRule { Alliance = "A" }));
    }

    private sealed class RuleServiceFixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();

        public RuleServiceFixture(params ObjectPriorityRule[] rules)
        {
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            Service = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            if (rules.Length > 0
                && !Service.SaveManifest(new ObjectPriorityRuleManifest { Rules = [.. rules] }))
            {
                throw new InvalidOperationException(Service.LastLoadStatus);
            }
        }

        public ObjectPriorityRuleService Service { get; }

        public void Dispose()
            => tempDirectory.Dispose();
    }

    public class NativePartyProxy : DispatchProxy
    {
        public nint Address;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_GroupManagerAddress"
                ? Address
                : throw new InvalidOperationException("Alliance resolution must use native group state only.");
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null
                || targetMethod.ReturnType == typeof(void)
                || !targetMethod.ReturnType.IsValueType)
            {
                return null;
            }

            return Activator.CreateInstance(targetMethod.ReturnType);
        }
    }
}
