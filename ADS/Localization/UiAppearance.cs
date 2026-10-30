using System.Numerics;
using ADS.Windows;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using AethertekUI.Dalamud;
using Dalamud.Interface.ImGuiFileDialog;

namespace ADS.Localization;

internal sealed class UiAppearance : IDisposable
{
    private readonly Plugin plugin;
    private readonly FileDialogManager imagePicker = new();
    private MaterialGlassRenderer? backgroundGlass;
    internal bool BackgroundRequested { get; private set; }
    internal bool StylingEnabled => plugin.Configuration.UiStylingEnabled;
    internal bool WindowsLocked => plugin.Configuration.UiWindowsLocked;
    internal UiAppearance(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.PluginInterface.UiBuilder.ResizeBuffers += ReleaseResources;
    }
    private AethertekUI.Dalamud.MaterialTextHost? shapedText;
    private MaterialTheme? theme;
    private readonly Dictionary<Dalamud.Interface.Windowing.Window, AethertekUI.Dalamud.MaterialAppearanceTitleBar> appearanceTitleBars = new();
    private readonly MaterialWindowFold fontStatusFold = new();
    private readonly MaterialWindowDecorations fontStatusDecorations = new();
    private readonly Dictionary<string, MaterialWindowOpacity> windowOpacities = new();
    private uint appliedAccent;
    private Vector3 accentDraft;
    private MaterialColorSettings colorDraft = new();
    private MaterialColorSettings? appliedColors;
    private MaterialColorSettings? appliedSavedColors;
    private bool fontErrorLogged;
    private MaterialUiSoundPlayer? uiSoundPlayer;
    private bool appliedSoundsMuted = true;
    private readonly Dictionary<Dalamud.Interface.Windowing.Window, Dalamud.Interface.Windowing.TitleBarButton> soundTitleButtons = new();

    internal void Draw()
    {
        var requested = false;
        var completed = false;
        try { imagePicker.Draw(); completed = DrawOwner(out requested); }
        finally { backgroundGlass?.EndFrame(requested && completed); BackgroundRequested = false; }
    }

    private bool DrawOwner(out bool requested)
    {
        requested = false;
        BackgroundRequested = false;
        Ui.SetLanguage(Ui.ResolveLanguage(plugin.Configuration.UiLanguage, Plugin.ClientState.ClientLanguage));
        if (!plugin.WindowSystem.Windows.Any(window => window.IsOpen)) return false;
        shapedText ??= new(Plugin.TextureProvider);
        using var text = shapedText.Push();
        var savedColors = plugin.Configuration.UiColors ?? new MaterialColorSettings();
        if (savedColors != appliedSavedColors)
        {
            colorDraft = savedColors;
            appliedSavedColors = savedColors;
        }
        if (theme is null || appliedAccent != (plugin.Configuration.UiAccentRgb & 0xFFFFFF) || colorDraft != appliedColors)
        {
            appliedAccent = plugin.Configuration.UiAccentRgb & 0xFFFFFF;
            appliedColors = colorDraft;
            theme = AdsPresentation.Theme(appliedAccent, colorDraft);
            theme.Feedback = PlayLcarsSound;
            if (!theme.IsLcars) uiSoundPlayer?.Stop();
            var rgb = AdsPresentation.Rgb(appliedAccent);
            accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        theme.Density = plugin.Configuration.UiCompact ? MaterialDensity.Compact : MaterialDensity.Standard;
        if (appliedSoundsMuted != plugin.Configuration.UiLcarsSoundsMuted)
        {
            appliedSoundsMuted = plugin.Configuration.UiLcarsSoundsMuted;
            if (appliedSoundsMuted) uiSoundPlayer?.Stop();
        }
        if (!plugin.Fonts.Prepare(Ui.Language, shapedText.Renderer))
        {
            if (!fontErrorLogged && plugin.Fonts.LoadException is { } error)
            { Plugin.Log.Error(error, "[ADS] Required UI fonts failed to load."); fontErrorLogged = true; }
            using var statusPalette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale,
                StylingEnabled ? MaterialStyleMode.ColorsOnly : MaterialStyleMode.Native);
            using var statusChrome = MaterialWindowChrome.Push();
            ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
            fontStatusFold.PreDraw("ADS##FontStatus", null, null, reducedMotion: !StylingEnabled,
                prepareDecorations: StylingEnabled ? fontStatusDecorations.Prepare : null);
            bool statusVisible;
            using (var backdrop = MaterialSkin.BackdropStyle())
                statusVisible = ImGui.Begin("ADS##FontStatus", ImGuiWindowFlags.AlwaysAutoResize |
                    (WindowsLocked ? ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize : ImGuiWindowFlags.None));
            try
            {
                if (statusVisible)
                {
                    if (StylingEnabled) fontStatusDecorations.Paint();
                    MaterialSkin.Backdrop();
                    if (theme.IsLcars) MaterialLcars.Header("ADS");
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
                if (StylingEnabled) fontStatusDecorations.Paint();
                fontStatusFold.PostDraw();
            ApplyWindowOpacity("ADS##FontStatus");
            }
            return false;
        }
        fontErrorLogged = false;
        var blurEnabled = StylingEnabled && colorDraft.HasBackgroundBlur(colorDraft.Mode);
        var imageEnabled = StylingEnabled && colorDraft.Mode == MaterialColorMode.Glassmorphism &&
            colorDraft.GlassBackground == MaterialGlassBackground.Image;
        requested = BackgroundRequested = blurEnabled || imageEnabled;
        if (requested) backgroundGlass ??= new(Plugin.PluginInterface, Plugin.Log);
        backgroundGlass?.ConfigureImage(Plugin.TextureProvider, imageEnabled, colorDraft.GlassImagePath);
        backgroundGlass?.BeginFrame();
        using var rendererScope = MaterialSkin.PushBackgroundRenderer(requested ? backgroundGlass : null,
            blurEnabled, colorDraft.BackgroundBlurPercent / 100f);
        using var palette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale,
                StylingEnabled ? MaterialStyleMode.ColorsOnly : MaterialStyleMode.Native);
        using var style = new MaterialStyleScope();
        var scale = ImGuiHelpers.GlobalScale;
        if (StylingEnabled)
        {
            style.Style(ImGuiStyleVar.WindowPadding, new Vector2(plugin.Configuration.UiCompact ? 8 : 20) * scale);
            style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(plugin.Configuration.UiCompact ? 6 : 12, plugin.Configuration.UiCompact ? 4 : 10) * scale);
            style.Style(ImGuiStyleVar.FramePadding, new Vector2(plugin.Configuration.UiCompact ? 6 : 14, plugin.Configuration.UiCompact ? 2 : 7) * scale);
            style.Style(ImGuiStyleVar.CellPadding, new Vector2(plugin.Configuration.UiCompact ? 4 : 10, plugin.Configuration.UiCompact ? 2 : 8) * scale);
            style.Style(ImGuiStyleVar.FrameRounding, 4 * scale);
            style.Style(ImGuiStyleVar.ChildRounding, 4 * scale);
        }
        using var body = plugin.Fonts.Push(UiFontRole.Body);
        using var chrome = MaterialWindowChrome.Push();
        if (StylingEnabled && theme.IsGlassmorphism)
            style.Color(ImGuiCol.ChildBg, MaterialColor.Alpha(theme.Colors.Surface, .42f));
        using var nativeButtons = MaterialSkin.NativeButtonStyle();
        PrepareAppearanceTitleBars(plugin.WindowSystem);
        plugin.WindowSystem.Draw();
        foreach (var (owner, titlebar) in appearanceTitleBars)
        {
            PrepareBackgroundOpacity(owner);
            titlebar.DrawPopups();
        }
        foreach (var window in plugin.WindowSystem.Windows)
            if (window.IsOpen)
            {
                if (StylingEnabled && window is Dalamud.Interface.Windowing.Window owner)
                    WindowLayout.PaintTitleWithImageAndStatus(owner,
                        plugin.Configuration.UiShowTitlebarStatus ? TitlebarStatus(owner) : null);
                ApplyWindowOpacity(window.WindowName);
            }
        return true;
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
        if (DrawColourEditor("ADSUiAppearance")) plugin.SaveConfiguration();
        MaterialText.Text(Ui.T("Language"));
        DrawLanguage();
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
        if (WindowLayout.BeginCombo("##ADSUiLanguage", LanguageMenuLabel((int)Ui.Language)))
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
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        var minimum = Math.Max(300 * MaterialTheme.Metrics.Scale,
            new[] { "Compact visible on main window", "Transparency visible on main window", "Language visible on main window",
                "Show titlebar status", "Detailed titlebar status", "Auto-fade when unfocused" }
                .Max(label => MaterialText.Measure(Ui.T(label)).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X));
        MaterialAppearanceTab.Draw("ADSGroupedAppearance", new(Ui.T("Theme & Language"), Ui.T("Main window controls"),
            Ui.T("Window opacity"), Ui.T("When unfocused")), minimum,
            () =>
            {
                DrawCompactToggle();
                ImGui.SameLine();
                MaterialText.Text(Ui.T("Compact mode"));
                DrawSelector(false);
            }, DrawMainWindowControls, DrawNormalOpacity, DrawUnfocusedOpacity);
        DrawLcarsSounds();
    }

    private void DrawMainWindowControls()
    {
        var styling = StylingEnabled;
        if (WindowLayout.Checkbox("Use styled UI", ref styling)) SetWindowControls(styling: styling);
        var locked = WindowsLocked;
        if (WindowLayout.Checkbox("Lock windows", ref locked)) SetWindowControls(locked: locked);
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
    }

    private void PrepareAppearanceTitleBars(Dalamud.Interface.Windowing.WindowSystem windows)
    {
        foreach (var window in windows.Windows.OfType<Dalamud.Interface.Windowing.Window>())
        {
            window.AllowBackgroundBlur = false;
            if (window is PositionedWindow positioned) positioned.Appearance = this;
            if ((window.Flags & ImGuiWindowFlags.NoTitleBar) != 0) continue;
            if (!appearanceTitleBars.ContainsKey(window))
            {
                appearanceTitleBars.Add(window, AethertekUI.Dalamud.MaterialAppearanceTitleBar.Register(window,
                    () => new(Ui.T("Language"), Ui.T("Compact mode"), Ui.T("Transparency"), Ui.T("Color")),
                    DrawLanguageSelector,
                    () => { plugin.Configuration.UiCompact = !plugin.Configuration.UiCompact; plugin.SaveConfiguration(); },
                    DrawTransparencySettings, DrawTitleBarColour,
                    () => window is global::ADS.Windows.MainWindow
                        ? new(plugin.Configuration.UiLanguageVisibleOnMainWindow, plugin.Configuration.UiCompactVisibleOnMainWindow,
                            plugin.Configuration.UiTransparencyVisibleOnMainWindow, true)
                        : new(true, true, true, true)));
                appearanceTitleBars[window].RegisterWindowControls(
                    () => SetWindowControls(styling: !StylingEnabled),
                    () => Ui.T(StylingEnabled ? "Use native UI" : "Use styled UI"),
                    () => SetWindowControls(locked: !WindowsLocked),
                    () => Ui.T(WindowsLocked ? "Unlock windows" : "Lock windows"));
            }
            appearanceTitleBars[window].RefreshVisibility();
            appearanceTitleBars[window].SetLcarsIcons(MaterialTheme.Current.IsLcars);
            if (!soundTitleButtons.TryGetValue(window, out var sound))
            {
                sound = new()
                {
                    Priority = (int)Math.Max(int.MinValue + 1L, window.TitleBarButtons.Select(static button => (long)button.Priority).DefaultIfEmpty(0).Min() - 1),
                    IconOffset = new(2, 1),
                    Click = mouse => { if (mouse == ImGuiMouseButton.Left && LcarsActive) SetLcarsSounds(plugin.Configuration.UiLcarsSoundsMuted); },
                    ShowTooltip = () => MaterialText.SetTooltip(Ui.T(plugin.Configuration.UiLcarsSoundsMuted ? "UI sounds muted" : "UI sounds on")),
                };
                soundTitleButtons.Add(window, sound);
            }
            sound.Icon = plugin.Configuration.UiLcarsSoundsMuted ? Dalamud.Interface.FontAwesomeIcon.VolumeMute : Dalamud.Interface.FontAwesomeIcon.VolumeUp;
            if (MaterialTheme.Current.IsLcars && !window.TitleBarButtons.Contains(sound)) window.TitleBarButtons.Add(sound);
            else if (!MaterialTheme.Current.IsLcars) window.TitleBarButtons.Remove(sound);
        }
    }

    private void DrawTitleBarColour()
    {
        using var controls = MaterialControls.Push(AdsPresentation.Controls(28));
        if (DrawColourEditor("ADSUiAppearance")) plugin.SaveConfiguration();
    }

    private bool DrawColourEditor(string id)
    {
        var changed = MaterialAppearanceSelector.DrawColors(id, ref colorDraft, ref accentDraft,
            new(Ui.T("Color"), Ui.T("Language"), Ui.T("Teal"), Ui.T("Blue"), Ui.T("Pink"), Ui.T("Custom RGB")),
            new(Ui.T("Auto"), Ui.T("Picker"), Ui.T("Default"), Ui.T("Background, controls and text follow your theme."),
                Ui.T("Theme colour"), Ui.T("Choose each colour independently."), Ui.T("Background"), Ui.T("Controls"),
                Ui.T("Text"), Ui.T("Aa  Preview text"), Ui.T("Control"),
                Ui.T("Switch to Picker to set each colour yourself."), Ui.T("Switch back to Auto to use the automatic theme."))
            {
                Lcars = Ui.T("LCARS"), LcarsHelp = Ui.T("LCARS panels, bars and controls"),
                Glassmorphism = Ui.T("Glassmorphism"), Spatial = Ui.T("Spatial"), Cyberpunk = Ui.T("Cyberpunk"),
                LcarsBar1 = Ui.T("LCARS bar 1"), LcarsBar2 = Ui.T("LCARS bar 2"), LcarsBar3 = Ui.T("LCARS bar 3"),
                Reset = Ui.T("Reset"), ResetColors = Ui.T("Reset colours"), Animation = Ui.T("Animation"),
                AnimationSpeed = Ui.T("Animation speed (0.1–10x; default 1x)"), WaveColors = Ui.T("Wave colours"),
                GlassWave1 = Ui.T("Wave colour 1"), GlassWave2 = Ui.T("Wave colour 2"), GlassWave3 = Ui.T("Wave colour 3"),
                ResetWaveColors = Ui.T("Reset wave colours"),
                BackgroundMode = Ui.T("Background mode"), Waves = Ui.T("Waves"), Lights = Ui.T("Lights"), Image = Ui.T("Image"),
                LightEffect = Ui.T("Light effect"), AmbientGlows = Ui.T("Ambient glows"),
                SweepingBeams = Ui.T("Sweeping beams"), PointerGlow = Ui.T("Pointer glow"),
                LightColors = Ui.T("Light colours"), GlassLight1 = Ui.T("Light colour 1"),
                GlassLight2 = Ui.T("Light colour 2"), GlassLight3 = Ui.T("Light colour 3"), ResetLightColors = Ui.T("Reset light colours"),
                ImageFile = Ui.T("Image file"), BrowseImage = Ui.T("Browse..."), LoadImage = Ui.T("Load image"),
                ImageHelp = Ui.T("The image fills the window. Blur (%) applies to the image."),
                BackgroundBlur = Ui.T("Background blur"), BlurPercent = Ui.T("Blur (%)"),
            }, new MaterialColorSettings(), MaterialColorSettings.Rgb(AdsPresentation.ReferenceAccent),
            BrowseImage,
            plugin.Configuration.UiCompact ? 298 : 316);
        DrawScreenshotPause(id + "-screenshots");
        if (backgroundGlass?.ImageProblem is { } problem && colorDraft.Mode == MaterialColorMode.Glassmorphism &&
            colorDraft.GlassBackground == MaterialGlassBackground.Image) MaterialText.TextWrapped(Ui.T(problem));
        if (!changed) return false;
        plugin.Configuration.UiColors = colorDraft;
        if (!LcarsActive) uiSoundPlayer?.Stop();
        plugin.Configuration.UiAccentRgb = MaterialColorSettings.ToRgb(accentDraft);
        return true;
    }

    private void DrawTransparencySettings()
    {
        DrawNormalOpacity();
        DrawUnfocusedOpacity();
    }

    private void DrawNormalOpacity()
    {
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        if (WindowLayout.Checkbox("Transparency", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.SaveConfiguration(); }
        MaterialText.Text(Ui.T("Opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var normalOpacity = plugin.Configuration.UiWindowOpacityPercent;
        if (WindowLayout.InputInt("##UiWindowOpacityPercent", ref normalOpacity))
        { plugin.Configuration.UiWindowOpacityPercent = normalOpacity; plugin.SaveConfiguration(); }
        DrawBlurControls();
    }

    private void DrawUnfocusedOpacity()
    {
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

    private bool LcarsActive => plugin.Configuration.UiColors?.Mode == MaterialColorMode.Lcars;

    private void PlayLcarsSound(MaterialUiSoundCue cue)
    {
        if (!LcarsActive || plugin.Configuration.UiLcarsSoundsMuted) return;
        (uiSoundPlayer ??= new MaterialUiSoundPlayer()).TryPlay(cue);
    }

    private void SetLcarsSounds(bool enabled)
    {
        plugin.Configuration.UiLcarsSoundsMuted = !enabled;
        if (!enabled) uiSoundPlayer?.Stop();
        plugin.SaveConfiguration();
    }

    private void DrawLcarsSounds()
    {
        MaterialSettings.Section(Ui.T("LCARS UI sounds"));
        ImGui.BeginDisabled(!LcarsActive);
        try
        {
            var enabled = !plugin.Configuration.UiLcarsSoundsMuted;
            if (WindowLayout.Checkbox("UI sounds", ref enabled)) SetLcarsSounds(enabled);
            MaterialText.TextWrapped(Ui.T("Custom UI tones. The titlebar speaker toggles the same setting."));
            ImGui.BeginDisabled(plugin.Configuration.UiLcarsSoundsMuted);
            try
            {
                var current = MaterialTheme.Current;
                var feedback = current.Feedback;
                bool preview;
                try { current.Feedback = null; preview = WindowLayout.Button(Ui.L("Preview sound")); }
                finally { current.Feedback = feedback; }
                MaterialLcars.Feedback(preview, MaterialUiSoundCue.Preview);
            }
            finally { ImGui.EndDisabled(); }
            if (uiSoundPlayer?.Error is not null) MaterialText.TextWrapped(Ui.T("Audio output is unavailable."));
        }
        finally { ImGui.EndDisabled(); }
        if (!LcarsActive) MaterialText.TextDisabled(Ui.T("Select LCARS to use UI sounds."));
        else if (plugin.Configuration.UiLcarsSoundsMuted) MaterialText.TextDisabled(Ui.T("Turn on UI sounds to preview."));
    }

    internal void DrawLcarsQuickPreferences()
    {
        foreach (var mode in new[] { MaterialColorMode.Auto, MaterialColorMode.Picker, MaterialColorMode.Lcars,
                     MaterialColorMode.Glassmorphism, MaterialColorMode.Spatial, MaterialColorMode.Cyberpunk })
        {
            var caption = mode == MaterialColorMode.Lcars ? "LCARS" : mode.ToString();
            if (mode != MaterialColorMode.Auto) WindowLayout.SameLineIfFits(MaterialText.Measure(Ui.T(caption)).X + ImGui.GetFrameHeight());
            if (WindowLayout.RadioButton(Ui.L(caption + "##MainColour"), plugin.Configuration.UiColors.Mode == mode)
                && plugin.Configuration.UiColors.Mode != mode)
            {
                plugin.Configuration.UiColors = plugin.Configuration.UiColors with { Mode = mode };
                if (!LcarsActive) uiSoundPlayer?.Stop();
                plugin.SaveConfiguration();
            }
        }
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("UI sounds")));
        ImGui.BeginDisabled(!LcarsActive);
        try
        {
            var enabled = !plugin.Configuration.UiLcarsSoundsMuted;
            if (WindowLayout.Checkbox("UI sounds", ref enabled)) SetLcarsSounds(enabled);
        }
        finally { ImGui.EndDisabled(); }
    }

    internal void DrawBackdrop(Dalamud.Interface.Windowing.Window owner)
    {
        if (!windowOpacities.TryGetValue(owner.WindowName, out var opacity))
            windowOpacities.Add(owner.WindowName, opacity = new());
        PrepareBackgroundOpacity(owner);
        MaterialSkin.Backdrop();
    }

    private void PrepareBackgroundOpacity(Dalamud.Interface.Windowing.Window owner)
    {
        if (backgroundGlass == null) return;
        var native = ImGuiP.FindWindowByName(owner.WindowName);
        backgroundGlass.Opacity = !plugin.Configuration.UiTransparencyEnabled ? 1 :
            !native.IsNull && !native.Appearing && windowOpacities.TryGetValue(owner.WindowName, out var opacity)
                ? opacity.CurrentOpacity : plugin.Configuration.UiWindowOpacityPercent / 100f;
    }

    private void SetWindowControls(bool? styling = null, bool? locked = null)
    {
        if ((!styling.HasValue || styling.Value == StylingEnabled) && (!locked.HasValue || locked.Value == WindowsLocked)) return;
        if (styling.HasValue) plugin.Configuration.UiStylingEnabled = styling.Value;
        if (locked.HasValue) plugin.Configuration.UiWindowsLocked = locked.Value;
        plugin.SaveConfiguration();
    }

    internal bool HandleCommand(string arguments)
    {
        var parts = arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].ToLowerInvariant() is not ("style" or "styling" or "lock" or "unlock")) return false;
        if (parts[0].ToLowerInvariant() is "lock" or "unlock")
        { SetWindowControls(locked: parts[0].Equals("lock", StringComparison.OrdinalIgnoreCase)); return true; }
        bool? selected = parts.Length == 1 ? !StylingEnabled : parts.Length == 2 ? parts[1].ToLowerInvariant() switch
        { "on" => true, "off" => false, "toggle" => !StylingEnabled, _ => (bool?)null } : null;
        if (!selected.HasValue) { Plugin.ChatGui.Print("/ads style [on|off|toggle], /ads lock, /ads unlock"); return true; }
        SetWindowControls(styling: selected.Value);
        return true;
    }

    private void BrowseImage()
    {
        var owner = plugin.Configuration;
        imagePicker.OpenFileDialog(Ui.T("Choose background image") + "###AdsBackgroundImagePicker",
            Ui.T("Images") + "{.png,.jpg,.jpeg,.bmp,.gif,.webp,.tif,.tiff,.dds,.tga},.*", (accepted, path) =>
            {
                if (!accepted || !ReferenceEquals(owner, plugin.Configuration)) return;
                owner.UiColors = (owner.UiColors ?? new MaterialColorSettings()) with { GlassImagePath = path };
                colorDraft = owner.UiColors;
                plugin.SaveConfiguration();
            });
    }

    private void DrawScreenshotPause(string id)
    {
        if (colorDraft.Mode == MaterialColorMode.Glassmorphism && colorDraft.GlassBackground == MaterialGlassBackground.Image)
        { MaterialText.TextWrapped(Ui.T("Image backgrounds remain visible to screenshots.")); return; }
        var paused = backgroundGlass?.ScreenshotsPaused == true;
        ImGui.BeginDisabled(!paused && backgroundGlass?.CanPauseForScreenshots != true);
        try
        {
            if (WindowLayout.Checkbox("Pause backdrop for screenshots###" + id, ref paused))
                backgroundGlass?.TrySetScreenshotsPaused(paused);
        }
        finally { ImGui.EndDisabled(); }
    }

    private void DrawBlurControls()
    {
        var settings = plugin.Configuration.UiColors ?? new MaterialColorSettings();
        var enabled = settings.HasBackgroundBlur(settings.Mode);
        var changed = WindowLayout.Checkbox("Background blur###background-blur", ref enabled);
        if (changed) settings = settings.WithBackgroundBlur(settings.Mode, enabled);
        var percent = settings.BackgroundBlurPercent;
        ImGui.BeginDisabled(!enabled);
        try
        {
            if (WindowLayout.InputInt("Blur (%)###background-frost", ref percent))
            { settings = settings with { BackgroundBlurPercent = percent }; changed = true; }
        }
        finally { ImGui.EndDisabled(); }
        if (changed) { plugin.Configuration.UiColors = colorDraft = settings; plugin.SaveConfiguration(); }
        DrawScreenshotPause("window-screenshots");
    }

    private void ReleaseResources() => backgroundGlass?.ReleaseResources();

    public void Dispose()
    {
        Plugin.PluginInterface.UiBuilder.ResizeBuffers -= ReleaseResources;
        imagePicker.Reset();
        backgroundGlass?.Dispose();
        uiSoundPlayer?.Dispose();
        shapedText?.Dispose();
    }
}
