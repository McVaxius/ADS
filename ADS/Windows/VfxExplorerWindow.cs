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
        Size = new Vector2(1580f, 920f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
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

        ImGui.TextUnformatted(Ui.T("ECommons VFX Trace"));
        ImGui.TextUnformatted(Ui.T("Territory / Map / CFC: {0} / {1} / {2}", context.TerritoryTypeId, context.MapId, context.ContentFinderConditionId));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("Pending: {0}", plugin.HigherLowerVfxTraceService.PendingCount));

        DrawFilters();
        DrawTrackedNow(context);

        ImGui.TextUnformatted(Ui.T("Rows shown: {0}", rows.Count));
        DrawEventTable(rows);
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint("##ADSVfxFilter", Ui.T("filter path / ids / params"), ref textFilter, 180);
        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Clear")))
            textFilter = string.Empty;
        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Clear Rows")))
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

        if (!ImGui.CollapsingHeader(Ui.L("Tracked now ({0})", tracked.Count), ImGuiTreeNodeFlags.DefaultOpen))
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
        ImGui.TableHeadersRow();

        foreach (var row in tracked)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.AgeSeconds.ToString("0.0s", CultureInfo.InvariantCulture));
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(row.Path) ? "-" : row.Path);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(row.CardSource) ? "-" : row.CardSource);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.Slot) ? "-" : row.Slot);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.TextureIndexText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.DecodedCardText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(row.SolverReason) ? "-" : row.SolverReason);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.CasterLabel);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.TargetLabel);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Ui.T("{0}/{1}", row.TerritoryId, row.MapId));
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.PositionText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.DistanceText);
            DrawTrackedTooltip(row);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.ScaleRotationText);
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
        ImGui.TableHeadersRow();

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
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
        DrawRowTooltip(row);
    }

    private static void DrawRowTooltip(HigherLowerVfxTraceService.VfxEventRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 80f);
        ImGui.TextUnformatted(row.KindLabel);
        ImGui.TextUnformatted(Ui.T("Time UTC: {0:O}", row.TimestampUtc));
        ImGui.TextUnformatted(Ui.T("Ptr: 0x{0:X}", row.Pointer));
        ImGui.TextWrapped(Ui.T("Path: {0}", (string.IsNullOrWhiteSpace(row.Path) ? "(blank)" : row.Path)));
        ImGui.TextWrapped($"Card: source={row.CardSource} slot={row.Slot} textureIndex={row.TextureIndexText} decoded={row.DecodedCardText} reason={row.SolverReason}");
        ImGui.TextWrapped(Ui.T("Caster: {0}", row.CasterLabel));
        ImGui.TextWrapped(Ui.T("Target: {0}", row.TargetLabel));
        ImGui.TextUnformatted(Ui.T("Caster base / target base: {0} / {1}", row.CasterBaseId, row.TargetBaseId));
        ImGui.TextUnformatted(Ui.T("Territory/map: {0}/{1}", row.TerritoryId, row.MapId));
        ImGui.TextUnformatted(Ui.T("Position: {0}", row.PositionText));
        ImGui.TextUnformatted(Ui.T("Distance: {0}", row.DistanceText));
        ImGui.TextWrapped(row.ScaleRotationText);
        ImGui.TextWrapped(Ui.T("Source params: {0}", row.SourceParams));
        ImGui.TextWrapped(Ui.T("HL relevant: {0}", row.HigherLowerRelevant));
        ImGui.TextWrapped(row.ToHldbgLogLine());
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void DrawTrackedTooltip(HigherLowerVfxTraceService.TrackedVfxRow row)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 80f);
        ImGui.TextUnformatted(Ui.T("VfxId: 0x{0:X}", row.VfxId));
        ImGui.TextWrapped(Ui.T("Path: {0}", (string.IsNullOrWhiteSpace(row.Path) ? "(blank)" : row.Path)));
        ImGui.TextWrapped($"Card: source={row.CardSource} slot={row.Slot} textureIndex={row.TextureIndexText} decoded={row.DecodedCardText} reason={row.SolverReason}");
        ImGui.TextWrapped(Ui.T("Caster: {0}", row.CasterLabel));
        ImGui.TextWrapped(Ui.T("Target: {0}", row.TargetLabel));
        ImGui.TextUnformatted(Ui.T("Territory/map: {0}/{1}", row.TerritoryId, row.MapId));
        ImGui.TextUnformatted(Ui.T("Position: {0}", row.PositionText));
        ImGui.TextUnformatted(Ui.T("Distance: {0}", row.DistanceText));
        ImGui.TextWrapped(row.ScaleRotationText);
        ImGui.TextWrapped(Ui.T("Static: {0} run: {1}", row.IsStatic, row.HasRun));
        ImGui.TextWrapped(Ui.T("HL relevant: {0}", row.HigherLowerRelevant));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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
