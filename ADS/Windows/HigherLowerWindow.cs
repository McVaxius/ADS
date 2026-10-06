using AethertekUI;
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
        windowMotion.DrawChrome();
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
        if (WindowLayout.SmallButton(Ui.L("Copy Live Probe JSON")))
            ImGui.SetClipboardText(plugin.GetHigherLowerLiveProbeJson());

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open Diagnostics Folder")))
        {
            Directory.CreateDirectory(probe.DiagnosticDirectory);
            plugin.OpenPath(probe.DiagnosticDirectory);
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Open Card Map")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe.CardMapPath)!);
            if (!File.Exists(probe.CardMapPath))
                File.WriteAllText(probe.CardMapPath, "{}");
            plugin.OpenPath(probe.CardMapPath);
        }

        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Clear Unsafe Calibration Map")))
            plugin.TreasureHighLowDiagnosticService.ClearUnsafeCalibrationMap();

        var vfxDataminingEnabled = plugin.TreasureHighLowDiagnosticService.VfxDataminingEnabled;
        if (WindowLayout.Checkbox("Experimental Higher/Lower datamining for treasure maps", ref vfxDataminingEnabled))
            plugin.TreasureHighLowDiagnosticService.SetVfxDataminingEnabled(vfxDataminingEnabled);
        MaterialText.TextWrapped(
            Ui.T("Datamine: enabled={0} session={1}", Ui.T(probe.VfxDataminingEnabled ? "YES" : "NO"), (string.IsNullOrWhiteSpace(probe.CurrentDatamineSessionDirectory) ? Ui.T("(not opened yet)") : probe.CurrentDatamineSessionDirectory)));
    }

    private static void DrawRuntime(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime,
        HigherLowerCardVfxSolverService.SolverState solverState,
        HigherLowerAutomationService.HigherLowerAutomationDebugState automationState)
    {
        MaterialText.Text(Ui.T("Live State"));
        MaterialText.Text(Ui.T("Active: {0}", Ui.T(runtime.Active ? "YES" : "NO")));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("TreasureHighLow: {0}", Ui.T(runtime.TreasureHighLowVisible ? "YES" : "NO")));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("_NotificationChallenge: {0}", Ui.T(runtime.NotificationChallengeVisible ? "YES" : "NO")));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("SelectYesno: {0}", Ui.T(runtime.SelectYesnoVisible ? "YES" : "NO")));
        MaterialText.Text(Ui.T("High targetable: {0}", Ui.T(runtime.HighTargetable ? "YES" : "NO")));
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Low targetable: {0}", Ui.T(runtime.LowTargetable ? "YES" : "NO")));
        MaterialText.TextWrapped(Ui.T("Prompt text: {0}", (string.IsNullOrWhiteSpace(runtime.SelectYesnoPrompt) ? Ui.T("(none)") : runtime.SelectYesnoPrompt)));
        MaterialText.TextWrapped(Ui.T("Addon cards: current={0} other={1} source={2}", runtime.AddonCurrentCardText, runtime.AddonOtherCardText, runtime.AddonCurrentCardSource));
        MaterialText.TextWrapped(Ui.T("Automation: {0}", Ui.Display(runtime.SafetyStatus)));
        MaterialText.TextWrapped(Ui.T("H/L auto: enabled={0} hold={1} dutyKey={2} step={3} card={4} action={5} source={6} directionSource={7} target={8}@{9} pendingTarget={10} pendingPhase={11} pendingAge={12} pendingPhaseAge={13} retryAttempts={14}/{15} lastInteractAge={16} nextRetryIn={17} retryReason={18} pendingBaselineRowSeq={19} terminalProof={20} callbackAction={21} callbackPhase={22} callbackAge={23} surface={24} blocked='{25}' retained={26} retainedStep={27} retainedCard={28} retainedAction={29}",
            Ui.T(automationState.Enabled ? "YES" : "NO"), Ui.T(automationState.HoldMovement ? "YES" : "NO"),
            automationState.SessionDutyKey, automationState.SessionStep, automationState.Card?.ToString(Ui.Culture) ?? Ui.T("unknown"),
            automationState.Action, automationState.Source, automationState.DirectionSource, automationState.DirectionTargetName,
            automationState.DirectionTargetDistance?.ToString("0.00", Ui.Culture) ?? Ui.T("unknown"),
            automationState.PendingDirectionTarget, automationState.PendingDirectionPhase,
            automationState.PendingDirectionAgeSeconds?.ToString("0.0", Ui.Culture) ?? Ui.T("none"),
            automationState.PendingDirectionPhaseAgeSeconds?.ToString("0.0", Ui.Culture) ?? Ui.T("none"),
            automationState.PendingDirectionInteractAttempts, automationState.PendingDirectionMaxInteractAttempts,
            automationState.PendingDirectionLastInteractAgeSeconds?.ToString("0.0", Ui.Culture) ?? Ui.T("none"),
            automationState.PendingDirectionNextRetryInSeconds?.ToString("0.0", Ui.Culture) ?? Ui.T("none"),
            automationState.PendingDirectionLastRetryReason, automationState.PendingDirectionBaselineServerRowSequence?.ToString(Ui.Culture) ?? Ui.T("none"),
            automationState.PendingDirectionTerminalProof, automationState.PendingCallbackAction, automationState.PendingCallbackPhase,
            automationState.PendingCallbackAgeSeconds?.ToString("0.0", Ui.Culture) ?? Ui.T("none"), automationState.Surface,
            Ui.Display(automationState.BlockedReason), Ui.T(automationState.Retained ? "YES" : "NO"),
            automationState.RetainedStep?.ToString(Ui.Culture) ?? Ui.T("none"), automationState.RetainedCard?.ToString(Ui.Culture) ?? Ui.T("unknown"),
            automationState.RetainedAction));
        MaterialText.TextWrapped(Ui.T("H/L solver: card={0} choice={1} confidence={2} source={3} sourceRowSeq={4} sourceState={5} reason='{6}' textureIndex={7} textureIndexSource={8}", solverState.CurrentCard?.ToString(Ui.Culture) ?? Ui.T("unknown"),
            solverState.RecommendedChoice, solverState.Confidence.ToString().ToLowerInvariant(), solverState.CardSource,
            solverState.SourceRowSequence?.ToString(Ui.Culture) ?? Ui.T("none"), solverState.SourceStateData,
            Ui.Display(solverState.Reason), solverState.TextureIndex?.ToString(Ui.Culture) ?? Ui.T("unknown"), solverState.TextureIndexSource));
    }

    private void DrawTagControls()
    {
        MaterialText.Text(Ui.T("Tag Board"));
        DrawCardSelector("Left", ref leftCard);
        DrawCardSelector("Right", ref rightCard);

        ImGui.SetNextItemWidth(220f);
        WindowLayout.InputText(Ui.L("Label"), ref tagLabel, 80);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Tag Board")))
        {
            if (plugin.TreasureHighLowDiagnosticService.TagKnownBoard(leftCard, rightCard, tagLabel))
                plugin.PrintStatus($"Higher/Lower board tag queued: left={leftCard} right={rightCard} label='{tagLabel}'.");
            else
                plugin.PrintStatus("Higher/Lower board tag failed; use 1-9, blank, or unknown.");
        }
    }

    private static void DrawCardSelector(string label, ref string selected)
    {
        MaterialText.Text(Ui.T(label));
        ImGui.SameLine();
        for (var card = 1; card <= 9; card++)
        {
            var value = card.ToString();
            if (WindowLayout.RadioButton(Ui.L("{0}##{1}{2}", value, label, value), selected == value))
                selected = value;
            ImGui.SameLine();
        }

        if (WindowLayout.RadioButton(Ui.L("blank##{0}blank", label), selected == "blank"))
            selected = "blank";
        ImGui.SameLine();
        if (WindowLayout.RadioButton(Ui.L("unknown##{0}unknown", label), selected == "unknown"))
            selected = "unknown";
    }

    private static void DrawCandidates(IReadOnlyList<TreasureHighLowDiagnosticService.HigherLowerBoardCandidate> candidates)
    {
        MaterialText.Text(Ui.T("Board Candidates"));
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
        WindowLayout.TableHeadersRow();

        foreach (var row in candidates)
        {
            ImGui.TableNextRow();
            DrawCell(row.Side);
            DrawCell(row.BaseId.ToString());
            DrawCell(row.LayoutId.ToString());
            DrawCell(row.GimmickId.ToString());
            DrawCell(row.EventState.ToString());
            DrawCell($"0x{row.EventId:X}");
            DrawCell(Ui.T(row.Targetable ? "YES" : "NO"));
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
            MaterialText.TextColored(new Vector4(1f, 0.44f, 0.35f, 1f), Ui.Display(warning));

        if (probe.SafetyWarnings.Count == 0)
            MaterialText.TextDisabled(Ui.T("No duplicate or unsafe board-key warnings."));
    }

    private static void DrawCardMap(IReadOnlyList<TreasureHighLowDiagnosticService.HigherLowerCardMapEntry> entries)
    {
        MaterialText.Text(Ui.T("Card Map ({0})", entries.Count));
        if (!ImGui.BeginTable("HLCardMap", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 220f)))
            return;

        ImGui.TableSetupColumn(Ui.L("card"), ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn(Ui.L("unsafe"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("source"), ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn(Ui.L("graphicKey"));
        WindowLayout.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            DrawCell(entry.Card.ToString());
            DrawCell(Ui.T(entry.Unsafe ? "YES" : "NO"));
            DrawCell(entry.Source);
            DrawCell(entry.GraphicKey);
        }

        ImGui.EndTable();
    }

    private static void DrawCell(string value)
    {
        ImGui.TableNextColumn();
        MaterialText.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }
}
