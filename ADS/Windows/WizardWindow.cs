using AethertekUI;
using ADS.Localization;
using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class WizardWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private string? selectedWizardId;
    private int pageIndex;

    public WizardWindow(Plugin plugin)
        : base("ADS Guided Setup###ADSSetupWizards")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560f, 440f),
            MaximumSize = new Vector2(1800f, 1400f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(640f, 520f) : new Vector2(760f, 620f);
    }

    public void Dispose()
    {
    }

    public void OpenHub()
    {
        selectedWizardId = null;
        pageIndex = 0;
        IsOpen = true;
    }

    public void OpenWizard(string wizardId)
    {
        selectedWizardId = WizardCatalog.All.Any(wizard => wizard.Id == wizardId) ? wizardId : null;
        pageIndex = 0;
        IsOpen = true;
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();
        var wizard = selectedWizardId is null
            ? null
            : WizardCatalog.All.FirstOrDefault(candidate => candidate.Id == selectedWizardId);
        if (wizard is null)
        {
            DrawHub();
            return;
        }

        DrawWizard(wizard);
    }

    private void DrawHub()
    {
        MaterialText.Text(Ui.T("Guided Setup"));
        MaterialText.TextWrapped(Ui.T("Choose any feature-specific setup flow. Completion is independent, optional, and every flow remains replayable."));
        ImGui.Spacing();

        foreach (var wizard in WizardCatalog.All)
        {
            var completed = WizardCatalog.IsCompleted(plugin.Configuration, wizard.Id);
            ImGui.Separator();
            MaterialText.Text(Ui.Display(wizard.Title));
            ImGui.SameLine();
            MaterialText.TextColored(completed ? new Vector4(0.35f, 0.85f, 0.45f, 1f) : new Vector4(0.75f, 0.75f, 0.75f, 1f), Ui.Display(completed ? "Completed" : "Optional"));
            MaterialText.TextWrapped(Ui.Display(wizard.Summary));
            if (WindowLayout.Button(Ui.L(Ui.T(completed ? "Replay" : "Start") + "###" + $"{(completed ? "Replay" : "Start")}##{wizard.Id}"), new Vector2(140f, 28f)))
            {
                selectedWizardId = wizard.Id;
                pageIndex = 0;
            }
        }
    }

    private void DrawWizard(WizardDefinition wizard)
    {
        pageIndex = Math.Clamp(pageIndex, 0, wizard.Pages.Count - 1);
        var page = wizard.Pages[pageIndex];
        if (WindowLayout.SmallButton(Ui.L("Back to setup hub")))
        {
            selectedWizardId = null;
            pageIndex = 0;
            return;
        }

        ImGui.Spacing();
        MaterialText.Text(Ui.Display(wizard.Title));
        MaterialText.TextDisabled(Ui.T("{0} / {1}: {2}", pageIndex + 1, wizard.Pages.Count, Ui.T(page.Title)));
        ImGui.Separator();
        MaterialText.TextWrapped(Ui.Display(page.Body));
        ImGui.Spacing();
        foreach (var step in page.Steps)
            MaterialText.BulletText(Ui.Display(step));

        if (page.Commands.Count > 0)
        {
            ImGui.Spacing();
            MaterialText.Text(Ui.T("Useful commands"));
            foreach (var command in page.Commands)
            {
                MaterialText.Text(command);
                ImGui.SameLine();
                if (WindowLayout.SmallButton(Ui.L("Copy##{0}-{1}-{2}", wizard.Id, page.Id, command)))
                    ImGui.SetClipboardText(command);
            }
        }

        ImGui.Spacing();
        DrawSafeNavigationButtons(wizard.Id);
        ImGui.Spacing();
        ImGui.Separator();

        using (new ImGuiDisabledBlock(pageIndex == 0))
        {
            if (WindowLayout.Button(Ui.L("Previous"), new Vector2(120f, 30f)))
                pageIndex--;
        }

        ImGui.SameLine();
        if (pageIndex + 1 < wizard.Pages.Count)
        {
            if (WindowLayout.Button(Ui.L("Next"), new Vector2(120f, 30f)))
                pageIndex++;
        }
        else if (WindowLayout.Button(Ui.L("Mark complete"), new Vector2(150f, 30f)))
        {
            WizardCatalog.SetCompleted(plugin.Configuration, wizard.Id);
            plugin.SaveConfiguration();
            selectedWizardId = null;
            pageIndex = 0;
        }
    }

    private void DrawSafeNavigationButtons(string wizardId)
    {
        switch (wizardId)
        {
            case WizardCatalog.DutyOperationsId:
            case WizardCatalog.DiagnosticsRecoveryId:
                if (WindowLayout.SmallButton(Ui.L("Open Main")))
                    plugin.OpenMainUi();
                break;
            case WizardCatalog.RulesDataId:
                if (WindowLayout.SmallButton(Ui.L("Open Rules")))
                    plugin.OpenRuleEditorUi();
                ImGui.SameLine();
                if (WindowLayout.SmallButton(Ui.L("Open Maturity")))
                    plugin.OpenDutyMaturityEditorUi();
                break;
            case WizardCatalog.UtilitiesId:
                if (WindowLayout.SmallButton(Ui.L("Open Desynthesis")))
                    plugin.OpenDesynthConfigUi();
                break;
            case WizardCatalog.TreasureFollowId:
                if (WindowLayout.SmallButton(Ui.L("Open Treasure Routes")))
                    plugin.OpenTreasureRouteEditorUi();
                ImGui.SameLine();
                if (WindowLayout.SmallButton(Ui.L("Open Higher/Lower")))
                    plugin.OpenHigherLowerUi();
                break;
        }
    }

    private readonly ref struct ImGuiDisabledBlock
    {
        private readonly bool disabled;

        public ImGuiDisabledBlock(bool disabled)
        {
            this.disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (disabled)
                ImGui.EndDisabled();
        }
    }
}
