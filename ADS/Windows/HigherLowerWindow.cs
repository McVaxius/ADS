using ADS.Localization;
using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class HigherLowerWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private string leftCard = "unknown";
    private string rightCard = "unknown";
    private string tagLabel = "ui";

    public HigherLowerWindow(Plugin plugin)
        : base("Higher/Lower###ADSHigherLower")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720f, 520f),
            MaximumSize = new Vector2(2400f, 1600f),
        };
        Size = new Vector2(980f, 760f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var probe = plugin.TreasureHighLowDiagnosticService.CaptureLiveProbe();
        var solverState = plugin.HigherLowerCardVfxSolverService.CurrentState;
        var automationState = plugin.HigherLowerAutomationService.CaptureDebugState();
        DrawToolbar(probe);
        ImGui.Separator();
        DrawRuntime(probe.Runtime, solverState, automationState);
        ImGui.Spacing();
        DrawTagControls();
        ImGui.Spacing();
        DrawCandidates(probe.BoardCandidates);
        ImGui.Spacing();
        DrawWarnings(probe);
        ImGui.Spacing();
        DrawCardMap(probe.CardMapEntries);
    }

    private void DrawToolbar(TreasureHighLowDiagnosticService.HigherLowerLiveProbe probe)
    {
        var diagnosticsEnabled = plugin.TreasureHighLowDiagnosticService.Enabled;
        if (WindowLayout.Checkbox("Diagnostics", ref diagnosticsEnabled))
            plugin.TreasureHighLowDiagnosticService.SetEnabled(diagnosticsEnabled);

        ImGui.SameLine();
        var automationEnabled = plugin.HigherLowerAutomationService.Enabled;
        if (WindowLayout.Checkbox("Automation", ref automationEnabled))
            plugin.HigherLowerAutomationService.SetEnabled(automationEnabled);

        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Copy Live Probe JSON")))
            ImGui.SetClipboardText(plugin.GetHigherLowerLiveProbeJson());

        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Open Diagnostics Folder")))
        {
            Directory.CreateDirectory(probe.DiagnosticDirectory);
            plugin.OpenPath(probe.DiagnosticDirectory);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Open Card Map")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe.CardMapPath)!);
            if (!File.Exists(probe.CardMapPath))
                File.WriteAllText(probe.CardMapPath, "{}");
            plugin.OpenPath(probe.CardMapPath);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Ui.L("Clear Unsafe Calibration Map")))
            plugin.TreasureHighLowDiagnosticService.ClearUnsafeCalibrationMap();

        var vfxDataminingEnabled = plugin.TreasureHighLowDiagnosticService.VfxDataminingEnabled;
        if (WindowLayout.Checkbox("Experimental Higher/Lower datamining for treasure maps", ref vfxDataminingEnabled))
            plugin.TreasureHighLowDiagnosticService.SetVfxDataminingEnabled(vfxDataminingEnabled);
        ImGui.TextWrapped(
            Ui.T("Datamine: enabled={0} session={1}", probe.VfxDataminingEnabled, (string.IsNullOrWhiteSpace(probe.CurrentDatamineSessionDirectory) ? "(not opened yet)" : probe.CurrentDatamineSessionDirectory)));
    }

    private static void DrawRuntime(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerAutomationService.HigherLowerAutomationDebugState automationState)
    {
        ImGui.TextUnformatted(Ui.T("Live State"));
        ImGui.TextUnformatted(Ui.T("Active: {0}", runtime.Active));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("TreasureHighLow: {0}", runtime.TreasureHighLowVisible));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("_NotificationChallenge: {0}", runtime.NotificationChallengeVisible));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("SelectYesno: {0}", runtime.SelectYesnoVisible));
        ImGui.TextUnformatted(Ui.T("High targetable: {0}", runtime.HighTargetable));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("Low targetable: {0}", runtime.LowTargetable));
        ImGui.TextWrapped(Ui.T("Prompt text: {0}", (string.IsNullOrWhiteSpace(runtime.SelectYesnoPrompt) ? "(none)" : runtime.SelectYesnoPrompt)));
        ImGui.TextWrapped($"Addon cards: current={runtime.AddonCurrentCardText} other={runtime.AddonOtherCardText} source={runtime.AddonCurrentCardSource}");
        ImGui.TextWrapped(Ui.T("Automation: {0}", Ui.Display(runtime.SafetyStatus)));
        ImGui.TextWrapped($"H/L auto: enabled={automationState.Enabled} hold={automationState.HoldMovement} dutyKey={automationState.SessionDutyKey} step={automationState.SessionStep} card={(automationState.Card?.ToString() ?? "unknown")} action={automationState.Action} source={automationState.Source} directionSource={automationState.DirectionSource} target={automationState.DirectionTargetName}@{(automationState.DirectionTargetDistance?.ToString("0.00") ?? "unknown")} pendingTarget={automationState.PendingDirectionTarget} pendingPhase={automationState.PendingDirectionPhase} pendingAge={(automationState.PendingDirectionAgeSeconds?.ToString("0.0") ?? "none")} pendingPhaseAge={(automationState.PendingDirectionPhaseAgeSeconds?.ToString("0.0") ?? "none")} retryAttempts={automationState.PendingDirectionInteractAttempts}/{automationState.PendingDirectionMaxInteractAttempts} lastInteractAge={(automationState.PendingDirectionLastInteractAgeSeconds?.ToString("0.0") ?? "none")} nextRetryIn={(automationState.PendingDirectionNextRetryInSeconds?.ToString("0.0") ?? "none")} retryReason={automationState.PendingDirectionLastRetryReason} pendingBaselineRowSeq={(automationState.PendingDirectionBaselineServerRowSequence?.ToString() ?? "none")} terminalProof={automationState.PendingDirectionTerminalProof} callbackAction={automationState.PendingCallbackAction} callbackPhase={automationState.PendingCallbackPhase} callbackAge={(automationState.PendingCallbackAgeSeconds?.ToString("0.0") ?? "none")} surface={automationState.Surface} blocked='{automationState.BlockedReason}' retained={automationState.Retained} retainedStep={(automationState.RetainedStep?.ToString() ?? "none")} retainedCard={(automationState.RetainedCard?.ToString() ?? "unknown")} retainedAction={automationState.RetainedAction}");
        ImGui.TextWrapped($"H/L solver: card={(solverState.CurrentCard?.ToString() ?? "unknown")} choice={solverState.RecommendedChoice} confidence={solverState.Confidence.ToString().ToLowerInvariant()} source={solverState.CardSource} sourceRowSeq={(solverState.SourceRowSequence?.ToString() ?? "none")} sourceState={solverState.SourceStateData} reason='{solverState.Reason}' textureIndex={(solverState.TextureIndex?.ToString() ?? "unknown")} textureIndexSource={solverState.TextureIndexSource}");
    }

    private void DrawTagControls()
    {
        ImGui.TextUnformatted(Ui.T("Tag Board"));
        DrawCardSelector("Left", ref leftCard);
        DrawCardSelector("Right", ref rightCard);

        ImGui.SetNextItemWidth(220f);
        ImGui.InputText(Ui.L("Label"), ref tagLabel, 80);
        ImGui.SameLine();
        if (ImGui.Button(Ui.L("Tag Board")))
        {
            if (plugin.TreasureHighLowDiagnosticService.TagKnownBoard(leftCard, rightCard, tagLabel))
                plugin.PrintStatus($"Higher/Lower board tag queued: left={leftCard} right={rightCard} label='{tagLabel}'.");
            else
                plugin.PrintStatus("Higher/Lower board tag failed; use 1-9, blank, or unknown.");
        }
    }

    private static void DrawCardSelector(string label, ref string selected)
    {
        ImGui.TextUnformatted(Ui.T(label));
        ImGui.SameLine();
        for (var card = 1; card <= 9; card++)
        {
            var value = card.ToString();
            if (ImGui.RadioButton(Ui.L("{0}##{1}{2}", value, label, value), selected == value))
                selected = value;
            ImGui.SameLine();
        }

        if (ImGui.RadioButton(Ui.L("blank##{0}blank", label), selected == "blank"))
            selected = "blank";
        ImGui.SameLine();
        if (ImGui.RadioButton(Ui.L("unknown##{0}unknown", label), selected == "unknown"))
            selected = "unknown";
    }

    private static void DrawCandidates(IReadOnlyList<TreasureHighLowDiagnosticService.HigherLowerBoardCandidate> candidates)
    {
        ImGui.TextUnformatted(Ui.T("Board Candidates"));
        if (!ImGui.BeginTable("HLBoardCandidates", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX))
            return;

        ImGui.TableSetupColumn(Ui.L("Side"));
        ImGui.TableSetupColumn(Ui.L("baseId"));
        ImGui.TableSetupColumn(Ui.L("layoutId"));
        ImGui.TableSetupColumn(Ui.L("gimmickId"));
        ImGui.TableSetupColumn(Ui.L("eventState"));
        ImGui.TableSetupColumn(Ui.L("eventId"));
        ImGui.TableSetupColumn(Ui.L("targetable"));
        ImGui.TableSetupColumn(Ui.L("position"));
        ImGui.TableSetupColumn(Ui.L("drawPtr"));
        ImGui.TableSetupColumn(Ui.L("draw signature"));
        ImGui.TableSetupColumn(Ui.L("graphicKey"));
        ImGui.TableHeadersRow();

        foreach (var row in candidates)
        {
            ImGui.TableNextRow();
            DrawCell(row.Side);
            DrawCell(row.BaseId.ToString());
            DrawCell(row.LayoutId.ToString());
            DrawCell(row.GimmickId.ToString());
            DrawCell(row.EventState.ToString());
            DrawCell($"0x{row.EventId:X}");
            DrawCell(row.Targetable.ToString());
            DrawCell(row.Position);
            DrawCell(row.DrawPointer);
            DrawCell(row.DrawSignature);
            DrawCell(row.GraphicKey);
        }

        ImGui.EndTable();
    }

    private static void DrawWarnings(TreasureHighLowDiagnosticService.HigherLowerLiveProbe probe)
    {
        foreach (var warning in probe.SafetyWarnings)
            ImGui.TextColored(new Vector4(1f, 0.44f, 0.35f, 1f), Ui.Display(warning));

        if (probe.SafetyWarnings.Count == 0)
            ImGui.TextDisabled(Ui.T("No duplicate or unsafe board-key warnings."));
    }

    private static void DrawCardMap(IReadOnlyList<TreasureHighLowDiagnosticService.HigherLowerCardMapEntry> entries)
    {
        ImGui.TextUnformatted(Ui.T("Card Map ({0})", entries.Count));
        if (!ImGui.BeginTable("HLCardMap", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 220f)))
            return;

        ImGui.TableSetupColumn(Ui.L("card"), ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn(Ui.L("unsafe"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("source"), ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn(Ui.L("graphicKey"));
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            DrawCell(entry.Card.ToString());
            DrawCell(entry.Unsafe.ToString());
            DrawCell(entry.Source);
            DrawCell(entry.GraphicKey);
        }

        ImGui.EndTable();
    }

    private static void DrawCell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }
}
