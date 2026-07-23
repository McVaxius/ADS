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
        Size = new Vector2(760f, 620f);
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

    public override void Draw()
    {
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
        ImGui.TextUnformatted("Guided Setup");
        ImGui.TextWrapped("Choose any feature-specific setup flow. Completion is independent, optional, and every flow remains replayable.");
        ImGui.Spacing();

        foreach (var wizard in WizardCatalog.All)
        {
            var completed = WizardCatalog.IsCompleted(plugin.Configuration, wizard.Id);
            ImGui.Separator();
            ImGui.TextUnformatted(wizard.Title);
            ImGui.SameLine();
            ImGui.TextColored(completed ? new Vector4(0.35f, 0.85f, 0.45f, 1f) : new Vector4(0.75f, 0.75f, 0.75f, 1f), completed ? "Completed" : "Optional");
            ImGui.TextWrapped(wizard.Summary);
            if (ImGui.Button($"{(completed ? "Replay" : "Start")}##{wizard.Id}", new Vector2(140f, 28f)))
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
        if (ImGui.SmallButton("Back to setup hub"))
        {
            selectedWizardId = null;
            pageIndex = 0;
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(wizard.Title);
        ImGui.TextDisabled($"{pageIndex + 1} / {wizard.Pages.Count}: {page.Title}");
        ImGui.Separator();
        ImGui.TextWrapped(page.Body);
        ImGui.Spacing();
        foreach (var step in page.Steps)
            ImGui.BulletText(step);

        if (page.Commands.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Useful commands");
            foreach (var command in page.Commands)
            {
                ImGui.TextUnformatted(command);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Copy##{wizard.Id}-{page.Id}-{command}"))
                    ImGui.SetClipboardText(command);
            }
        }

        ImGui.Spacing();
        DrawSafeNavigationButtons(wizard.Id);
        ImGui.Spacing();
        ImGui.Separator();

        using (new ImGuiDisabledBlock(pageIndex == 0))
        {
            if (ImGui.Button("Previous", new Vector2(120f, 30f)))
                pageIndex--;
        }

        ImGui.SameLine();
        if (pageIndex + 1 < wizard.Pages.Count)
        {
            if (ImGui.Button("Next", new Vector2(120f, 30f)))
                pageIndex++;
        }
        else if (ImGui.Button("Mark complete", new Vector2(150f, 30f)))
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
                if (ImGui.SmallButton("Open Main"))
                    plugin.OpenMainUi();
                break;
            case WizardCatalog.RulesDataId:
                if (ImGui.SmallButton("Open Rules"))
                    plugin.OpenRuleEditorUi();
                ImGui.SameLine();
                if (ImGui.SmallButton("Open Maturity"))
                    plugin.OpenDutyMaturityEditorUi();
                break;
            case WizardCatalog.UtilitiesId:
                if (ImGui.SmallButton("Open Desynthesis"))
                    plugin.OpenDesynthConfigUi();
                break;
            case WizardCatalog.TreasureFollowId:
                if (ImGui.SmallButton("Open Treasure Routes"))
                    plugin.OpenTreasureRouteEditorUi();
                ImGui.SameLine();
                if (ImGui.SmallButton("Open Higher/Lower"))
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
