using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ObjectExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private string filter = string.Empty;

    public ObjectExplorerWindow(Plugin plugin)
        : base("ADS Object Explorer###ADSObjectExplorer")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1040f, 900f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            ImGui.TextUnformatted("No local player is available.");
            return;
        }

        ImGui.TextUnformatted("Live Loaded Objects");
        ImGui.TextWrapped("Distances are 3D world distances from your current position. Y dist is the absolute world-space vertical difference. Hover a row for base id, object id, coordinates, and targetable state. CREATE RULE seeds a new rules-editor row with the current duty scope, current live layer, object kind, base id, and exact name.");
        ImGui.TextUnformatted($"Player: {localPlayer.Position.X:0.00}, {localPlayer.Position.Y:0.00}, {localPlayer.Position.Z:0.00}");
        ImGui.TextWrapped($"Flag status: {plugin.ObjectExplorerStatus}");

        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##ADSObjectFilter", "filter by name or kind", ref filter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            filter = string.Empty;

        var rows = BuildRows(localPlayer)
            .Where(MatchesFilter)
            .OrderBy(x => x.Distance)
            .ToList();

        ImGui.TextUnformatted($"Objects shown: {rows.Count}");
        if (!ImGui.BeginTable("ADSObjectExplorerTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("ObjectKind", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Y dist", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Flag", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Name);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.ObjectKind);
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.Distance.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.VerticalDelta.ToString("0.00"));
            DrawRowTooltip(row);

            ImGui.TableSetColumnIndex(4);
            if (ImGui.SmallButton($"[FLAG]##ADSObjectFlag{i}"))
                plugin.TryPlaceObjectFlag(row.Name, row.Position);

            ImGui.TableSetColumnIndex(5);
            if (ImGui.SmallButton($"CREATE RULE##ADSObjectCreateRule{i}"))
                plugin.CreateRuleFromExplorer(row.Name, row.ObjectKind, row.BaseId, row.Position);
        }

        ImGui.EndTable();
    }

    private IEnumerable<ObjectExplorerRow> BuildRows(IGameObject localPlayer)
    {
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject is null)
                continue;

            if (gameObject.GameObjectId == localPlayer.GameObjectId)
                continue;

            var name = gameObject.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            yield return new ObjectExplorerRow(
                name,
                gameObject.ObjectKind.ToString(),
                Vector3.Distance(localPlayer.Position, gameObject.Position),
                MathF.Abs(gameObject.Position.Y - localPlayer.Position.Y),
                gameObject.BaseId,
                gameObject.GameObjectId,
                gameObject.IsTargetable,
                gameObject.Position);
        }
    }

    private bool MatchesFilter(ObjectExplorerRow row)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.ObjectKind.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawRowTooltip(ObjectExplorerRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(row.Name);
        ImGui.TextUnformatted($"ObjectKind: {row.ObjectKind}");
        ImGui.TextUnformatted($"Distance: {row.Distance:0.00}");
        ImGui.TextUnformatted($"Y delta: {row.VerticalDelta:0.00}");
        ImGui.TextUnformatted($"BaseId: {row.BaseId}");
        ImGui.TextUnformatted($"GameObjectId: {row.GameObjectId}");
        ImGui.TextUnformatted($"Targetable: {(row.IsTargetable ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Position: {row.Position.X:0.00}, {row.Position.Y:0.00}, {row.Position.Z:0.00}");
        ImGui.EndTooltip();
    }

    private sealed record ObjectExplorerRow(
        string Name,
        string ObjectKind,
        float Distance,
        float VerticalDelta,
        uint BaseId,
        ulong GameObjectId,
        bool IsTargetable,
        Vector3 Position);
}
