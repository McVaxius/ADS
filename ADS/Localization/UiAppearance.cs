using System.Numerics;
using ADS.Windows;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace ADS.Localization;

internal sealed class UiAppearance(Plugin plugin) : IDisposable
{
    private AethertekUI.Dalamud.MaterialTextHost? shapedText;
    private MaterialTheme? theme;
    private readonly Dictionary<Dalamud.Interface.Windowing.Window, AethertekUI.Dalamud.MaterialAppearanceTitleBar> appearanceTitleBars = new();
    private readonly MaterialWindowFold fontStatusFold = new();
    private readonly MaterialWindowDecorations fontStatusDecorations = new();
    private readonly Dictionary<string, MaterialWindowOpacity> windowOpacities = new();
    private uint appliedAccent;
    private Vector3 accentDraft;
    private bool fontErrorLogged;

    internal void Draw()
    {
        Ui.SetLanguage(Ui.ResolveLanguage(plugin.Configuration.UiLanguage, Plugin.ClientState.ClientLanguage));
        if (!plugin.WindowSystem.Windows.Any(window => window.IsOpen)) return;
        shapedText ??= new(Plugin.TextureProvider);
        using var text = shapedText.Push();
        if (theme is null || appliedAccent != (plugin.Configuration.UiAccentRgb & 0xFFFFFF))
        {
            appliedAccent = plugin.Configuration.UiAccentRgb & 0xFFFFFF;
            theme = AdsPresentation.Theme(appliedAccent);
            var rgb = AdsPresentation.Rgb(appliedAccent);
            accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        theme.Density = plugin.Configuration.UiCompact ? MaterialDensity.Compact : MaterialDensity.Standard;
        if (!plugin.Fonts.Prepare(Ui.Language, shapedText.Renderer))
        {
            if (!fontErrorLogged && plugin.Fonts.LoadException is { } error)
            { Plugin.Log.Error(error, "[ADS] Required UI fonts failed to load."); fontErrorLogged = true; }
            using var statusPalette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
            using var statusChrome = MaterialWindowChrome.Push();
            ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
            fontStatusFold.PreDraw("ADS##FontStatus", null, null, reducedMotion: false,
                prepareDecorations: fontStatusDecorations.Prepare);
            try
            {
                if (ImGui.Begin("ADS##FontStatus", ImGuiWindowFlags.AlwaysAutoResize))
                {
                    fontStatusDecorations.Paint();
                    var failed = plugin.Fonts.LoadException is not null;
                    ImGui.TextWrapped(Ui.Language == UiLanguage.Hindi && failed ? "Hindi UI fonts are unavailable. Use English to continue."
                        : failed ? "UI fonts failed to load. See the plugin log." : "Loading UI fonts...");
                    if (Ui.Language == UiLanguage.Hindi && failed && ImGui.Button("Use English"))
                        plugin.SetUiLanguage(UiLanguage.English);
                }
            }
            finally
            {
                ImGui.End();
                fontStatusDecorations.Paint();
                fontStatusFold.PostDraw();
            ApplyWindowOpacity("ADS##FontStatus");
            }
            return;
        }
        fontErrorLogged = false;
        using var palette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
        using var style = new MaterialStyleScope();
        var scale = ImGuiHelpers.GlobalScale;
        style.Style(ImGuiStyleVar.WindowPadding, new Vector2(plugin.Configuration.UiCompact ? 8 : 20) * scale);
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(plugin.Configuration.UiCompact ? 6 : 12, plugin.Configuration.UiCompact ? 4 : 10) * scale);
        style.Style(ImGuiStyleVar.FramePadding, new Vector2(plugin.Configuration.UiCompact ? 6 : 14, plugin.Configuration.UiCompact ? 2 : 7) * scale);
        style.Style(ImGuiStyleVar.CellPadding, new Vector2(plugin.Configuration.UiCompact ? 4 : 10, plugin.Configuration.UiCompact ? 2 : 8) * scale);
        style.Style(ImGuiStyleVar.FrameRounding, 4 * scale);
        style.Style(ImGuiStyleVar.ChildRounding, 4 * scale);
        using var body = plugin.Fonts.Push(UiFontRole.Body);
        using var chrome = MaterialWindowChrome.Push();
        PrepareAppearanceTitleBars(plugin.WindowSystem);
        plugin.WindowSystem.Draw();
        foreach (var titlebar in appearanceTitleBars.Values) titlebar.DrawPopups();
        foreach (var window in plugin.WindowSystem.Windows)
            if (window.IsOpen)
            {
                if (window is Dalamud.Interface.Windowing.Window owner)
                    WindowLayout.PaintTitleWithImageAndStatus(owner,
                        plugin.Configuration.UiShowTitlebarStatus ? TitlebarStatus(owner) : null);
                ApplyWindowOpacity(window.WindowName);
            }
    }

    private MaterialTitlebarStatus TitlebarStatus(Dalamud.Interface.Windowing.Window owner)
    {
        if (owner is ObjectExplorerWindow explorer) return explorer.TitlebarStatus;
        if (owner is InventoryUtilityWindow)
        {
            var utility = plugin.UtilityAutomationService;
            var shortStatus = Ui.T(utility.IsRunning ? "Titlebar.Running" : "Stopped");
            var message = Ui.Display(utility.StatusMessage);
            return new(plugin.Configuration.UiDetailedTitlebarStatus ? shortStatus + " · " + message : shortStatus,
                shortStatus, message, utility.IsRunning ? MaterialTheme.Current.Colors.Primary : MaterialTheme.Current.Colors.OnSurfaceVariant);
        }
        var execution = plugin.ExecutionService;
        var state = Ui.T(execution.CurrentMode switch
        {
            ADS.Models.OwnershipMode.Failed => "Blocked",
            ADS.Models.OwnershipMode.Idle => "Stopped",
            ADS.Models.OwnershipMode.Observing => "Observing",
            ADS.Models.OwnershipMode.Leaving => "Leaving",
            _ => "Titlebar.Running",
        });
        var phase = Ui.T(execution.CurrentPhase switch
        {
            ADS.Models.ExecutionPhase.Idle or ADS.Models.ExecutionPhase.ObservingOnly => "Idle",
            ADS.Models.ExecutionPhase.Failure => "Blocked",
            ADS.Models.ExecutionPhase.LeavingDuty => "Leaving",
            ADS.Models.ExecutionPhase.CombatHold or ADS.Models.ExecutionPhase.MountedDutyCombat => "Combat",
            ADS.Models.ExecutionPhase.NavigatingToMonsterObjective or ADS.Models.ExecutionPhase.NavigatingToInteractableObjective
                or ADS.Models.ExecutionPhase.ApproachingInteractableObjective or ADS.Models.ExecutionPhase.NavigatingToRecoveryObjective
                or ADS.Models.ExecutionPhase.NavigatingToFrontierObjective or ADS.Models.ExecutionPhase.NavigatingToFollowObjective
                or ADS.Models.ExecutionPhase.NavigatingToMapXzDestination or ADS.Models.ExecutionPhase.NavigatingToXyzDestination => "Moving",
            ADS.Models.ExecutionPhase.AttemptingInteractableObjective => "Interacting",
            _ => "Titlebar.Waiting",
        });
        var duty = plugin.DutyContextService.Current.CurrentDuty is { } currentDuty ? Ui.DutyName(currentDuty) : null;
        var concise = string.Join(" · ", new[] { state, duty, phase }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var detail = Ui.Display(execution.LastStatus);
        return new(plugin.Configuration.UiDetailedTitlebarStatus ? concise + " · " + detail : concise,
            concise, concise + "\n" + detail, execution.CurrentMode == ADS.Models.OwnershipMode.Failed
                || execution.CurrentPhase == ADS.Models.ExecutionPhase.Failure ? MaterialTheme.Current.Colors.Error
                : execution.CurrentMode is ADS.Models.OwnershipMode.Idle or ADS.Models.OwnershipMode.Observing
                    ? MaterialTheme.Current.Colors.OnSurfaceVariant : MaterialTheme.Current.Colors.Primary);
    }

    internal void DrawCompactToggle()
    {
        var compact = plugin.Configuration.UiCompact;
        MaterialLayout.FitNextItemWidth(0, ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure("C").X);
        if (WindowLayout.NativeCheckbox("C###ADSUiCompact", ref compact))
        { plugin.Configuration.UiCompact = compact; plugin.SaveConfiguration(); }
        if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("Compact mode"));
    }

    internal void DrawSelector(bool includeCompact = true)
    {
        if (includeCompact)
        {
            DrawCompactToggle();
            ImGui.SameLine();
        }
        using var controls = MaterialControls.Push(AdsPresentation.Controls(28));
        var change = MaterialAppearanceSelector.Draw("ADSUiAppearance", ref accentDraft,
            new(Ui.T("Color"), Ui.T("Language"), Ui.T("Teal"), Ui.T("Blue"), Ui.T("Pink"), Ui.T("Custom RGB")), DrawLanguage);
        if (change.AccentChanged)
            plugin.Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
                | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8)
                | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        if (change.AccentChanged) plugin.SaveConfiguration();
    }

    internal void DrawLanguageSelector()
    {
        using var controls = MaterialControls.Push(AdsPresentation.Controls(28));
        DrawLanguage();
    }

    internal IEnumerable<string> LanguageMenuLabels
        => Ui.LanguageLabels.Select((_, index) => LanguageMenuLabel(index));

    private string LanguageMenuLabel(int index)
        => (UiLanguage)index == UiLanguage.Hindi && !plugin.Fonts.HindiMenuAvailable ? "Hindi (unavailable)" : Ui.LanguageLabels[index];

    private bool DrawLanguage()
    {
        using var picker = plugin.Fonts.PushPicker(Ui.Language);
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(130 * scale, LanguageMenuLabels.Max(label => MaterialText.Measure(label).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(width, width));
        var changed = false;
        if (MaterialText.BeginCombo("##ADSUiLanguage", LanguageMenuLabel((int)Ui.Language)))
        {
            try {
            for (var index = 0; index < Ui.LanguageLabels.Length; index++)
            {
                var language = (UiLanguage)index;
                var unavailable = language == UiLanguage.Hindi && !plugin.Fonts.HindiMenuAvailable;
                if (MaterialText.Selectable($"{Ui.LanguageLabels[index]}##ADSUiLanguage{index}", language == Ui.Language,
                    flags: unavailable ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None, display: LanguageMenuLabel(index)))
                {
                    plugin.SetUiLanguage(language);
                    changed = language != Ui.Language;
                }
            }

            } finally { ImGui.EndCombo(); }
        }
        return changed;
    }

    private void ApplyWindowOpacity(string windowName)
    {
        if (!windowOpacities.TryGetValue(windowName, out var opacity))
            windowOpacities.Add(windowName, opacity = new MaterialWindowOpacity());
        opacity.Apply(windowName, plugin.Configuration.UiWindowOpacityPercent / 100f,
            plugin.Configuration.UiTransparencyEnabled, plugin.Configuration.UiAutoFade,
            plugin.Configuration.UiFadedOpacityPercent / 100f, plugin.Configuration.UiUnfocusedDelaySeconds);
    }

    internal void DrawTransparencyToggle()
    {
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        if (WindowLayout.Checkbox("Transparency##MainWindow", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.SaveConfiguration(); }
    }

    internal void DrawWindowAppearanceSettings()
    {
        if (!MaterialText.CollapsingHeader(Ui.T("Window appearance") + "###UiWindowAppearance")) return;
        DrawCompactToggle();
        ImGui.SameLine();
        MaterialText.Text(Ui.T("Compact mode"));
        DrawSelector(false);
        var showStatus = plugin.Configuration.UiShowTitlebarStatus;
        if (WindowLayout.Checkbox("Show titlebar status", ref showStatus))
        { plugin.Configuration.UiShowTitlebarStatus = showStatus; plugin.SaveConfiguration(); }
        var detailedStatus = plugin.Configuration.UiDetailedTitlebarStatus;
        if (WindowLayout.Checkbox("Detailed titlebar status", ref detailedStatus))
        { plugin.Configuration.UiDetailedTitlebarStatus = detailedStatus; plugin.SaveConfiguration(); }
        var compactVisible = plugin.Configuration.UiCompactVisibleOnMainWindow;
        if (WindowLayout.Checkbox("Compact visible on main window", ref compactVisible))
        { plugin.Configuration.UiCompactVisibleOnMainWindow = compactVisible; plugin.SaveConfiguration(); }
        var transparencyVisible = plugin.Configuration.UiTransparencyVisibleOnMainWindow;
        if (WindowLayout.Checkbox("Transparency visible on main window", ref transparencyVisible))
        { plugin.Configuration.UiTransparencyVisibleOnMainWindow = transparencyVisible; plugin.SaveConfiguration(); }
        var languageVisible = plugin.Configuration.UiLanguageVisibleOnMainWindow;
        if (WindowLayout.Checkbox("Language visible on main window", ref languageVisible))
        { plugin.Configuration.UiLanguageVisibleOnMainWindow = languageVisible; plugin.SaveConfiguration(); }
        DrawTransparencySettings();
    }

    private void PrepareAppearanceTitleBars(Dalamud.Interface.Windowing.WindowSystem windows)
    {
        foreach (var window in windows.Windows.OfType<Dalamud.Interface.Windowing.Window>())
        {
            if ((window.Flags & ImGuiWindowFlags.NoTitleBar) != 0) continue;
            if (!appearanceTitleBars.ContainsKey(window))
                appearanceTitleBars.Add(window, AethertekUI.Dalamud.MaterialAppearanceTitleBar.Register(window,
                    () => new(Ui.T("Language"), Ui.T("Compact mode"), Ui.T("Transparency"), Ui.T("Color")),
                    DrawLanguageSelector,
                    () => { plugin.Configuration.UiCompact = !plugin.Configuration.UiCompact; plugin.SaveConfiguration(); },
                    DrawTransparencySettings, DrawTitleBarColour,
                    () => window is global::ADS.Windows.MainWindow
                        ? new(plugin.Configuration.UiLanguageVisibleOnMainWindow, plugin.Configuration.UiCompactVisibleOnMainWindow,
                            plugin.Configuration.UiTransparencyVisibleOnMainWindow, true)
                        : new(true, true, true, true)));
            appearanceTitleBars[window].RefreshVisibility();
        }
    }

    private void DrawTitleBarColour()
    {
        if (!MaterialAppearanceSelector.DrawPalette("ADSUiAppearance", ref accentDraft,
                new(Ui.T("Color"), Ui.T("Language"), Ui.T("Teal"), Ui.T("Blue"), Ui.T("Pink"), Ui.T("Custom RGB")))) return;
        plugin.Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
            | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8)
            | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        plugin.SaveConfiguration();
    }

    private void DrawTransparencySettings()
    {
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        if (WindowLayout.Checkbox("Transparency", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.SaveConfiguration(); }
        MaterialText.Text(Ui.T("Opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var normalOpacity = plugin.Configuration.UiWindowOpacityPercent;
        if (WindowLayout.InputInt("##UiWindowOpacityPercent", ref normalOpacity))
        { plugin.Configuration.UiWindowOpacityPercent = normalOpacity; plugin.SaveConfiguration(); }
        var autoFade = plugin.Configuration.UiAutoFade;
        if (WindowLayout.Checkbox("Auto-fade when unfocused", ref autoFade))
        { plugin.Configuration.UiAutoFade = autoFade; plugin.SaveConfiguration(); }
        ImGui.BeginDisabled(!autoFade);
        try
        {
        MaterialText.Text(Ui.T("Unfocused opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var fadedOpacity = plugin.Configuration.UiFadedOpacityPercent;
        if (WindowLayout.InputInt("##UiFadedOpacityPercent", ref fadedOpacity))
        { plugin.Configuration.UiFadedOpacityPercent = fadedOpacity; plugin.SaveConfiguration(); }
        MaterialText.Text(Ui.T("Unfocused delay (seconds)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var delay = plugin.Configuration.UiUnfocusedDelaySeconds;
        if (WindowLayout.InputInt("##UiUnfocusedDelaySeconds", ref delay))
        { plugin.Configuration.UiUnfocusedDelaySeconds = delay; plugin.SaveConfiguration(); }
        }
        finally { ImGui.EndDisabled(); }
    }

    public void Dispose() => shapedText?.Dispose();
}
