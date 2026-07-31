using System.Reflection;
using System.Text;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using ADS.Windows;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class AllianceRuleScopeTests
{
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
                Alliance = ruleAlliance,
                ObjectKind = ObjectKind.EventObj.ToString(),
                ObjectName = "Scoped Door",
                NameMatchMode = "Exact",
                Classification = nameof(InteractableClass.Required),
            });

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
                Alliance = ruleAlliance,
                Classification = nameof(InteractableClass.MapXzDestination),
                MapCoordinates = "11.3,10.4",
            });

        var matches = fixture.Service.GetMapXzDestinationRules(
            TestDutyContextFactory.Create(alliance: liveAlliance));

        Assert.Equal(expected, matches.Count == 1);
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

        var persistedJson = File.ReadAllText(fixture.Service.ConfigPath);
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
