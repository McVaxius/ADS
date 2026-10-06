using AethertekUI;
using ADS.Localization;
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
    private string actionStatus = "Ready.";

    public ServerEventExplorerWindow(Plugin plugin)
        : base("ADS Server Events###ADSServerEvents")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1050f, 480f),
            MaximumSize = new Vector2(3400f, 2200f),
        };
        Size = new Vector2(1500f, 900f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
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

        MaterialText.Text(Ui.T("BossMod-Style Server Event Trace"));
        MaterialText.Text(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));
        MaterialText.Text(Ui.T("Hooks: {0} installed", plugin.HigherLowerServerEventTraceService.InstalledHookCount));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Pending: {0}", plugin.HigherLowerServerEventTraceService.PendingCount));

        DrawLoggingAndExportControls();
        DrawHookStatus();
        DrawFilters();

        MaterialText.Text(Ui.T("Rows shown: {0}", rows.Count));
        MaterialText.TextWrapped(Ui.T("Action status: {0}", Ui.Display(actionStatus)));
        DrawTable(rows);
    }

    private void DrawLoggingAndExportControls()
    {
        var diskLoggingEnabled = plugin.TreasureHighLowDiagnosticService.VfxDataminingEnabled;
        if (WindowLayout.Checkbox("JSONL logging (disk)", ref diskLoggingEnabled))
        {
            plugin.TreasureHighLowDiagnosticService.SetVfxDataminingEnabled(diskLoggingEnabled);
            actionStatus = diskLoggingEnabled
                ? "JSONL logging enabled. Files are written only during active Higher/Lower datamine sessions."
                : "JSONL logging disabled and the active writer was closed.";
        }

        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("Persistent datamine JSONL logging. Off by default. Enabling this can use substantial disk space."));

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open JSONL")))
        {
            var path = plugin.TreasureHighLowDiagnosticService.FindLatestDatamineJsonlPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                actionStatus = "No datamine.jsonl file exists yet.";
            }
            else
            {
                plugin.OpenPath(path);
                actionStatus = $"Opened {path}";
            }
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open Log Folder")))
        {
            Directory.CreateDirectory(plugin.TreasureHighLowDiagnosticService.DatamineDirectory);
            plugin.OpenPath(plugin.TreasureHighLowDiagnosticService.DatamineDirectory);
            actionStatus = $"Opened {plugin.TreasureHighLowDiagnosticService.DatamineDirectory}";
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Export All JSON")))
        {
            var result = plugin.ExportExplorerSnapshot();
            actionStatus = result.Status;
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open Export Folder")))
        {
            Directory.CreateDirectory(plugin.ExplorerSnapshotExportService.ExportDirectory);
            plugin.OpenPath(plugin.ExplorerSnapshotExportService.ExportDirectory);
            actionStatus = $"Opened {plugin.ExplorerSnapshotExportService.ExportDirectory}";
        }

        if (diskLoggingEnabled)
            MaterialText.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), Ui.T("JSONL DISK LOGGING IS ON"));
    }

    private void DrawHookStatus()
    {
        if (!MaterialText.CollapsingHeader(Ui.L("Hook status")))
            return;

        foreach (var line in plugin.HigherLowerServerEventTraceService.HookStatus)
            MaterialText.TextWrapped(line);
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(340f);
        WindowLayout.InputTextWithHint("##ADSServerEventsFilter", Ui.T("filter text / ids / params / data"), ref textFilter, 160);
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear")))
            textFilter = string.Empty;
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear Rows")))
            plugin.HigherLowerServerEventTraceService.Clear();
        ImGui.SameLine();
        WindowLayout.Checkbox("Newest First", ref newestFirst);

        WindowLayout.Checkbox("Current territory/map only", ref currentTerritoryMapOnly);
        ImGui.SameLine();
        WindowLayout.Checkbox("Higher/Lower relevant only", ref higherLowerRelevantOnly);

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
        if (WindowLayout.Checkbox($"{label}##ADSServerEventKind{kind}", ref enabled))
            kindFilters[kind] = enabled;
    }

    private void DrawTable(IReadOnlyList<HigherLowerServerEventTraceService.ServerEventRow> rows)
    {
        if (!ImGui.BeginTable("ADSServerEventTable", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Age/Time"), ImGuiTableColumnFlags.WidthFixed, 155f);
        ImGui.TableSetupColumn(Ui.L("Kind"), ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn(Ui.L("Actor/Object"), ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn(Ui.L("State/Data"), ImGuiTableColumnFlags.WidthStretch, 300f);
        ImGui.TableSetupColumn(Ui.L("Terr/Map"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("Position"), ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn(Ui.L("Dist"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Source Params"), ImGuiTableColumnFlags.WidthStretch, 380f);
        ImGui.TableSetupColumn(Ui.L("Actions"), ImGuiTableColumnFlags.WidthFixed, 290f);
        WindowLayout.TableHeadersRow();

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

            ImGui.TableSetColumnIndex(8);
            DrawActions(row);
        }

        ImGui.EndTable();
    }

    private void DrawCell(string value, HigherLowerServerEventTraceService.ServerEventRow row)
    {
        var displayValue = string.IsNullOrWhiteSpace(value) ? "-" : value;
        MaterialText.TextWrapped(displayValue);
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText(displayValue);
            actionStatus = $"Copied {row.KindLabel} cell.";
        }

        DrawRowTooltip(row);
    }

    private void DrawActions(HigherLowerServerEventTraceService.ServerEventRow row)
    {
        var hasPosition = row.Position.HasValue;
        ImGui.BeginDisabled(!hasPosition);
        if (WindowLayout.SmallButton(Ui.L("move##ADSServerEventMove{0}", row.Sequence)) && row.Position.HasValue)
        {
            plugin.TryExplorerNavigation(row.Position.Value, useFly: false);
            actionStatus = plugin.ObjectExplorerStatus;
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("fly##ADSServerEventFly{0}", row.Sequence)) && row.Position.HasValue)
        {
            plugin.TryExplorerNavigation(row.Position.Value, useFly: true);
            actionStatus = plugin.ObjectExplorerStatus;
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("FLAG##ADSServerEventFlag{0}", row.Sequence)) && row.Position.HasValue)
        {
            var label = string.IsNullOrWhiteSpace(row.ObjectName)
                ? $"{row.KindLabel} 0x{row.ActorId:X8}"
                : row.ObjectName;
            plugin.TryPlaceObjectFlag(label, row.Position.Value);
            actionStatus = plugin.ObjectExplorerStatus;
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("XYZ##ADSServerEventXyz{0}", row.Sequence)) && row.Position.HasValue)
        {
            ImGui.SetClipboardText(row.PositionText);
            actionStatus = $"Copied {row.PositionText}";
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("LINE##ADSServerEventLine{0}", row.Sequence)))
        {
            ImGui.SetClipboardText(row.ToBossModLogLine());
            actionStatus = $"Copied {row.KindLabel} log line.";
        }
    }

    private static void DrawRowTooltip(HigherLowerServerEventTraceService.ServerEventRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 70f);
        try {
        MaterialText.Text(Ui.T("{0} / {1}", row.BossModKind, row.KindLabel));
        MaterialText.Text(Ui.T("Time UTC: {0:O}", row.TimestampUtc));
        MaterialText.Text(Ui.T("Actor: 0x{0:X8}", row.ActorId));
        MaterialText.Text(Ui.T("Target: 0x{0:X}", row.TargetId));
        MaterialText.Text(Ui.T("Object name: {0}", (string.IsNullOrWhiteSpace(row.ObjectName) ? "(blank)" : row.ObjectName)));
        MaterialText.Text(Ui.T("Object id: 0x{0:X}", row.GameObjectId));
        MaterialText.Text(Ui.T("Entity id: 0x{0:X8}", row.EntityId));
        MaterialText.Text(Ui.T("Base id: {0}", row.BaseId));
        MaterialText.Text(Ui.T("Object kind: {0}", row.ObjectKind));
        MaterialText.Text(Ui.T("Layout id: {0}", row.LayoutId));
        MaterialText.Text(Ui.T("Gimmick id: {0}", row.GimmickId));
        MaterialText.Text(Ui.T("Event state: {0}", row.EventState));
        MaterialText.Text(Ui.T("Event id: 0x{0:X}", row.EventId));
        MaterialText.Text(Ui.T("Targetable: {0}", row.Targetable?.ToString() ?? "unknown"));
        MaterialText.Text(Ui.T("Territory/map: {0}/{1}", row.TerritoryId, row.MapId));
        MaterialText.Text(Ui.T("Position: {0}", row.PositionText));
        MaterialText.Text(Ui.T("Distance: {0}", row.DistanceText));
        MaterialText.TextWrapped(Ui.T("State/data: {0}", row.StateData));
        MaterialText.TextWrapped(Ui.T("Source params: {0}", row.SourceParams));
        MaterialText.TextWrapped(Ui.T("HL relevant: {0}", row.HigherLowerRelevant));
        MaterialText.TextWrapped(row.ToBossModLogLine());

        } finally { ImGui.PopTextWrapPos(); }

        } finally { ImGui.EndTooltip(); }
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
