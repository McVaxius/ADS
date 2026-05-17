using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ServerEventExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<HigherLowerServerEventTraceService.ServerEventKind, bool> kindFilters = new()
    {
        [HigherLowerServerEventTraceService.ServerEventKind.EObjAnim] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.LegacyMapEffect] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.MapEffect] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.EObjState] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.Timeline] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.SystemLog] = true,
        [HigherLowerServerEventTraceService.ServerEventKind.OpenTreasure] = true,
    };

    private string textFilter = string.Empty;
    private bool currentTerritoryMapOnly;
    private bool higherLowerRelevantOnly;
    private bool newestFirst = true;

    public ServerEventExplorerWindow(Plugin plugin)
        : base("ADS Server Events###ADSServerEvents")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900f, 480f),
            MaximumSize = new Vector2(3400f, 2200f),
        };
        Size = new Vector2(1500f, 900f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var context = plugin.DutyContextService.Current;
        var rows = plugin.HigherLowerServerEventTraceService.GetRowsSnapshot()
            .Where(row => !currentTerritoryMapOnly || MatchesCurrentTerritoryMap(row, context))
            .Where(row => !higherLowerRelevantOnly || row.HigherLowerRelevant)
            .Where(row => kindFilters.GetValueOrDefault(row.Kind, true))
            .Where(row => row.MatchesText(textFilter))
            .ToList();

        rows = newestFirst
            ? rows.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Sequence).ToList()
            : rows.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Sequence).ToList();

        ImGui.TextUnformatted("BossMod-Style Server Event Trace");
        ImGui.TextUnformatted($"Territory / Map / CFC: {context.TerritoryTypeId} / {context.MapId} / {context.ContentFinderConditionId}");
        ImGui.TextUnformatted($"Hooks: {plugin.HigherLowerServerEventTraceService.InstalledHookCount} installed");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Pending: {plugin.HigherLowerServerEventTraceService.PendingCount}");

        DrawHookStatus();
        DrawFilters();

        ImGui.TextUnformatted($"Rows shown: {rows.Count}");
        DrawTable(rows);
    }

    private void DrawHookStatus()
    {
        if (!ImGui.CollapsingHeader("Hook status"))
            return;

        foreach (var line in plugin.HigherLowerServerEventTraceService.HookStatus)
            ImGui.TextWrapped(line);
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(340f);
        ImGui.InputTextWithHint("##ADSServerEventsFilter", "filter text / ids / params / data", ref textFilter, 160);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            textFilter = string.Empty;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Rows"))
            plugin.HigherLowerServerEventTraceService.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Newest First", ref newestFirst);

        ImGui.Checkbox("Current territory/map only", ref currentTerritoryMapOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Higher/Lower relevant only", ref higherLowerRelevantOnly);

        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.EObjAnim, "EObjAnim");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.LegacyMapEffect, "LegacyMapEffect");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.MapEffect, "MapEffect");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.EObjState, "EObjState");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.Timeline, "Timeline");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.SystemLog, "SystemLog");
        ImGui.SameLine();
        DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind.OpenTreasure, "OpenTreasure");
    }

    private void DrawKindToggle(HigherLowerServerEventTraceService.ServerEventKind kind, string label)
    {
        var enabled = kindFilters.GetValueOrDefault(kind, true);
        if (ImGui.Checkbox($"{label}##ADSServerEventKind{kind}", ref enabled))
            kindFilters[kind] = enabled;
    }

    private void DrawTable(IReadOnlyList<HigherLowerServerEventTraceService.ServerEventRow> rows)
    {
        if (!ImGui.BeginTable("ADSServerEventTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("Age/Time", ImGuiTableColumnFlags.WidthFixed, 155f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Actor/Object", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("State/Data", ImGuiTableColumnFlags.WidthStretch, 300f);
        ImGui.TableSetupColumn("Terr/Map", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Source Params", ImGuiTableColumnFlags.WidthStretch, 380f);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawCell($"{Math.Max(0, (now - row.TimestampUtc).TotalSeconds):0.0}s {row.TimestampUtc:HH:mm:ss.fff}", row);

            ImGui.TableSetColumnIndex(1);
            DrawCell($"{row.BossModKind} / {row.KindLabel}", row);

            ImGui.TableSetColumnIndex(2);
            DrawCell(row.ActorLabel, row);

            ImGui.TableSetColumnIndex(3);
            DrawCell(row.StateData, row);

            ImGui.TableSetColumnIndex(4);
            DrawCell($"{row.TerritoryId}/{row.MapId}", row);

            ImGui.TableSetColumnIndex(5);
            DrawCell(row.PositionText, row);

            ImGui.TableSetColumnIndex(6);
            DrawCell(row.DistanceText, row);

            ImGui.TableSetColumnIndex(7);
            DrawCell(row.SourceParams, row);
        }

        ImGui.EndTable();
    }

    private static void DrawCell(string value, HigherLowerServerEventTraceService.ServerEventRow row)
    {
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
        DrawRowTooltip(row);
    }

    private static void DrawRowTooltip(HigherLowerServerEventTraceService.ServerEventRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 70f);
        ImGui.TextUnformatted($"{row.BossModKind} / {row.KindLabel}");
        ImGui.TextUnformatted($"Time UTC: {row.TimestampUtc:O}");
        ImGui.TextUnformatted($"Actor: 0x{row.ActorId:X8}");
        ImGui.TextUnformatted($"Target: 0x{row.TargetId:X}");
        ImGui.TextUnformatted($"Object name: {(string.IsNullOrWhiteSpace(row.ObjectName) ? "(blank)" : row.ObjectName)}");
        ImGui.TextUnformatted($"Object id: 0x{row.GameObjectId:X}");
        ImGui.TextUnformatted($"Entity id: 0x{row.EntityId:X8}");
        ImGui.TextUnformatted($"Base id: {row.BaseId}");
        ImGui.TextUnformatted($"Object kind: {row.ObjectKind}");
        ImGui.TextUnformatted($"Layout id: {row.LayoutId}");
        ImGui.TextUnformatted($"Gimmick id: {row.GimmickId}");
        ImGui.TextUnformatted($"Event state: {row.EventState}");
        ImGui.TextUnformatted($"Event id: 0x{row.EventId:X}");
        ImGui.TextUnformatted($"Targetable: {row.Targetable?.ToString() ?? "unknown"}");
        ImGui.TextUnformatted($"Territory/map: {row.TerritoryId}/{row.MapId}");
        ImGui.TextUnformatted($"Position: {row.PositionText}");
        ImGui.TextUnformatted($"Distance: {row.DistanceText}");
        ImGui.TextWrapped($"State/data: {row.StateData}");
        ImGui.TextWrapped($"Source params: {row.SourceParams}");
        ImGui.TextWrapped($"HL relevant: {row.HigherLowerRelevant}");
        ImGui.TextWrapped(row.ToBossModLogLine());
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static bool MatchesCurrentTerritoryMap(
        HigherLowerServerEventTraceService.ServerEventRow row,
        ADS.Models.DutyContextSnapshot context)
    {
        if (context.TerritoryTypeId != 0 && row.TerritoryId != context.TerritoryTypeId)
            return false;

        return context.MapId == 0 || row.MapId == 0 || row.MapId == context.MapId;
    }
}
