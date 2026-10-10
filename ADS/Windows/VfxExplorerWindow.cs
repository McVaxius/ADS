using AethertekUI;
using ADS.Localization;
using System.Globalization;
using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class VfxExplorerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<HigherLowerVfxTraceService.VfxEventKind, bool> kindFilters = Enum
        .GetValues<HigherLowerVfxTraceService.VfxEventKind>()
        .ToDictionary(x => x, _ => true);

    private string textFilter = string.Empty;
    private bool currentTerritoryMapOnly;
    private bool higherLowerRelevantOnly;
    private bool newestFirst = true;

    public VfxExplorerWindow(Plugin plugin)
        : base("ADS VFX###ADSVfx")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980f, 520f),
            MaximumSize = new Vector2(3600f, 2200f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(1080f, 620f) : new Vector2(1580f, 920f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();

        var context = plugin.DutyContextService.Current;
        var rows = plugin.HigherLowerVfxTraceService.GetRowsSnapshot()
            .Where(row => !currentTerritoryMapOnly || MatchesCurrentTerritoryMap(row, context))
            .Where(row => !higherLowerRelevantOnly || row.HigherLowerRelevant)
            .Where(row => kindFilters.GetValueOrDefault(row.Kind, true))
            .Where(row => row.MatchesText(textFilter))
            .ToList();

        rows = newestFirst
            ? rows.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Sequence).ToList()
            : rows.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Sequence).ToList();

        MaterialText.Text(Ui.T("ECommons VFX Trace"));
        MaterialText.Text(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Pending: {0}", plugin.HigherLowerVfxTraceService.PendingCount));

        DrawFilters();
        DrawTrackedNow(context);

        MaterialText.Text(Ui.T("Rows shown: {0}", rows.Count));
        DrawEventTable(rows);
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(360f);
        WindowLayout.InputTextWithHint("##ADSVfxFilter", Ui.T("filter path / ids / params"), ref textFilter, 180);
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear")))
            textFilter = string.Empty;
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear Rows")))
            plugin.HigherLowerVfxTraceService.Clear();
        ImGui.SameLine();
        WindowLayout.Checkbox("Newest First", ref newestFirst);

        WindowLayout.Checkbox("Current territory/map only", ref currentTerritoryMapOnly);
        ImGui.SameLine();
        WindowLayout.Checkbox("Higher/Lower relevant only", ref higherLowerRelevantOnly);

        foreach (var kind in Enum.GetValues<HigherLowerVfxTraceService.VfxEventKind>())
        {
            DrawKindToggle(kind, kind.ToString());
            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private void DrawKindToggle(HigherLowerVfxTraceService.VfxEventKind kind, string label)
    {
        var enabled = kindFilters.GetValueOrDefault(kind, true);
        if (WindowLayout.Checkbox($"{label}##ADSVfxKind{kind}", ref enabled))
            kindFilters[kind] = enabled;
    }

    private void DrawTrackedNow(DutyContextSnapshot context)
    {
        var tracked = plugin.HigherLowerVfxTraceService.GetTrackedSnapshot(context)
            .Where(row => !currentTerritoryMapOnly || MatchesCurrentTerritoryMap(row, context))
            .Where(row => !higherLowerRelevantOnly || row.HigherLowerRelevant)
            .Where(row => MatchesText(row, textFilter))
            .Take(80)
            .ToList();

        if (!MaterialText.CollapsingHeader(Ui.L("Tracked now ({0})", tracked.Count), ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!ImGui.BeginTable("ADSVfxTrackedNow", 13, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Age"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Path"), ImGuiTableColumnFlags.WidthStretch, 380f);
        ImGui.TableSetupColumn(Ui.L("cardSource"), ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn(Ui.L("slot"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("textureIndex"), ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupColumn(Ui.L("decodedCard"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("solverReason"), ImGuiTableColumnFlags.WidthStretch, 220f);
        ImGui.TableSetupColumn(Ui.L("Caster"), ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn(Ui.L("Target"), ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn(Ui.L("Terr/Map"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("Position"), ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn(Ui.L("Dist"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Scale/Rotation"), ImGuiTableColumnFlags.WidthStretch, 260f);
        WindowLayout.TableHeadersRow();

        foreach (var row in tracked)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            MaterialText.Text(row.AgeSeconds.ToString("0.0s", CultureInfo.InvariantCulture));
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(string.IsNullOrWhiteSpace(row.Path) ? "-" : row.Path);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(string.IsNullOrWhiteSpace(row.CardSource) ? "-" : row.CardSource);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(string.IsNullOrWhiteSpace(row.Slot) ? "-" : row.Slot);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(row.TextureIndexText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(row.DecodedCardText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(string.IsNullOrWhiteSpace(row.SolverReason) ? "-" : row.SolverReason);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(row.CasterLabel);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(row.TargetLabel);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(Ui.T("{0}/{1}", row.TerritoryId, row.MapId));
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(row.PositionText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.Text(row.DistanceText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            MaterialText.TextWrapped(row.ScaleRotationText);
            DrawTrackedTooltip(row);
        }

        ImGui.EndTable();
    }

    private static void DrawEventTable(IReadOnlyList<HigherLowerVfxTraceService.VfxEventRow> rows)
    {
        if (!ImGui.BeginTable("ADSVfxEvents", 15, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn(Ui.L("Age/Time"), ImGuiTableColumnFlags.WidthFixed, 155f);
        ImGui.TableSetupColumn(Ui.L("Kind"), ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn(Ui.L("Path"), ImGuiTableColumnFlags.WidthStretch, 380f);
        ImGui.TableSetupColumn(Ui.L("cardSource"), ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn(Ui.L("slot"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("textureIndex"), ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupColumn(Ui.L("decodedCard"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("solverReason"), ImGuiTableColumnFlags.WidthStretch, 220f);
        ImGui.TableSetupColumn(Ui.L("Caster"), ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn(Ui.L("Target"), ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn(Ui.L("Terr/Map"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(Ui.L("Position"), ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn(Ui.L("Dist"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Scale/Rotation"), ImGuiTableColumnFlags.WidthStretch, 260f);
        ImGui.TableSetupColumn(Ui.L("Source Params"), ImGuiTableColumnFlags.WidthStretch, 380f);
        WindowLayout.TableHeadersRow();

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawCell($"{Math.Max(0, (now - row.TimestampUtc).TotalSeconds):0.0}s {row.TimestampUtc:HH:mm:ss.fff}", row);

            ImGui.TableSetColumnIndex(1);
            DrawCell(row.KindLabel, row);

            ImGui.TableSetColumnIndex(2);
            DrawCell(row.Path, row);

            ImGui.TableSetColumnIndex(3);
            DrawCell(row.CardSource, row);

            ImGui.TableSetColumnIndex(4);
            DrawCell(row.Slot, row);

            ImGui.TableSetColumnIndex(5);
            DrawCell(row.TextureIndexText, row);

            ImGui.TableSetColumnIndex(6);
            DrawCell(row.DecodedCardText, row);

            ImGui.TableSetColumnIndex(7);
            DrawCell(row.SolverReason, row);

            ImGui.TableSetColumnIndex(8);
            DrawCell(row.CasterLabel, row);

            ImGui.TableSetColumnIndex(9);
            DrawCell(row.TargetLabel, row);

            ImGui.TableSetColumnIndex(10);
            DrawCell($"{row.TerritoryId}/{row.MapId}", row);

            ImGui.TableSetColumnIndex(11);
            DrawCell(row.PositionText, row);

            ImGui.TableSetColumnIndex(12);
            DrawCell(row.DistanceText, row);

            ImGui.TableSetColumnIndex(13);
            DrawCell(row.ScaleRotationText, row);

            ImGui.TableSetColumnIndex(14);
            DrawCell(row.SourceParams, row);
        }

        ImGui.EndTable();
    }

    private static void DrawCell(string value, HigherLowerVfxTraceService.VfxEventRow row)
    {
        MaterialText.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
        DrawRowTooltip(row);
    }

    private static void DrawRowTooltip(HigherLowerVfxTraceService.VfxEventRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 80f);
        try {
        MaterialText.Text(row.KindLabel);
        MaterialText.Text(Ui.T("Time UTC: {0:O}", row.TimestampUtc));
        MaterialText.Text(Ui.T("Ptr: 0x{0:X}", row.Pointer));
        MaterialText.TextWrapped(Ui.T("Path: {0}", (string.IsNullOrWhiteSpace(row.Path) ? "(blank)" : row.Path)));
        MaterialText.TextWrapped(Ui.T("Card: source={0} slot={1} textureIndex={2} decoded={3} reason={4}", row.CardSource, row.Slot,
            row.TextureIndex?.ToString(Ui.Culture) ?? Ui.T("unknown"), row.DecodedCard?.ToString(Ui.Culture) ?? Ui.T("unknown"), Ui.Display(row.SolverReason)));
        MaterialText.TextWrapped(Ui.T("Caster: {0}", row.CasterLabel));
        MaterialText.TextWrapped(Ui.T("Target: {0}", row.TargetLabel));
        MaterialText.Text(Ui.T("Caster base / target base: {0} / {1}", row.CasterBaseId, row.TargetBaseId));
        MaterialText.Text(Ui.T("Territory/map: {0}/{1}", row.TerritoryId, row.MapId));
        MaterialText.Text(Ui.T("Position: {0}", row.PositionText));
        MaterialText.Text(Ui.T("Distance: {0}", row.DistanceText));
        MaterialText.TextWrapped(row.ScaleRotationText);
        MaterialText.TextWrapped(Ui.T("Source params: {0}", row.SourceParams));
        MaterialText.TextWrapped(Ui.T("HL relevant: {0}", row.HigherLowerRelevant));
        MaterialText.TextWrapped(row.ToHldbgLogLine());

        } finally { ImGui.PopTextWrapPos(); }

        } finally { ImGui.EndTooltip(); }
    }

    private static void DrawTrackedTooltip(HigherLowerVfxTraceService.TrackedVfxRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        try {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 80f);
        try {
        MaterialText.Text(Ui.T("VfxId: 0x{0:X}", row.VfxId));
        MaterialText.TextWrapped(Ui.T("Path: {0}", (string.IsNullOrWhiteSpace(row.Path) ? "(blank)" : row.Path)));
        MaterialText.TextWrapped(Ui.T("Card: source={0} slot={1} textureIndex={2} decoded={3} reason={4}", row.CardSource, row.Slot,
            row.TextureIndex?.ToString(Ui.Culture) ?? Ui.T("unknown"), row.DecodedCard?.ToString(Ui.Culture) ?? Ui.T("unknown"), Ui.Display(row.SolverReason)));
        MaterialText.TextWrapped(Ui.T("Caster: {0}", row.CasterLabel));
        MaterialText.TextWrapped(Ui.T("Target: {0}", row.TargetLabel));
        MaterialText.Text(Ui.T("Territory/map: {0}/{1}", row.TerritoryId, row.MapId));
        MaterialText.Text(Ui.T("Position: {0}", row.PositionText));
        MaterialText.Text(Ui.T("Distance: {0}", row.DistanceText));
        MaterialText.TextWrapped(row.ScaleRotationText);
        MaterialText.TextWrapped(Ui.T("Static: {0} run: {1}", row.IsStatic, row.HasRun));
        MaterialText.TextWrapped(Ui.T("HL relevant: {0}", row.HigherLowerRelevant));

        } finally { ImGui.PopTextWrapPos(); }

        } finally { ImGui.EndTooltip(); }
    }

    private static bool MatchesText(HigherLowerVfxTraceService.TrackedVfxRow row, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        return row.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.CasterLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.TargetLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.CardSource.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Slot.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.SolverReason.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.TextureIndexText.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.DecodedCardText.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.VfxId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.CasterId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.TargetId.ToString("X", CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesCurrentTerritoryMap(
        HigherLowerVfxTraceService.VfxEventRow row,
        DutyContextSnapshot context)
    {
        if (context.TerritoryTypeId != 0 && row.TerritoryId != context.TerritoryTypeId)
            return false;

        return context.MapId == 0 || row.MapId == 0 || row.MapId == context.MapId;
    }

    private static bool MatchesCurrentTerritoryMap(
        HigherLowerVfxTraceService.TrackedVfxRow row,
        DutyContextSnapshot context)
    {
        if (context.TerritoryTypeId != 0 && row.TerritoryId != context.TerritoryTypeId)
            return false;

        return context.MapId == 0 || row.MapId == 0 || row.MapId == context.MapId;
    }
}
