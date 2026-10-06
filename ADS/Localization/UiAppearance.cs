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
                    MaterialText.TextWrapped(Ui.T(plugin.Fonts.LoadException is null ? "Loading UI fonts..." : "UI fonts failed to load. See the plugin log."));
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
        style.Style(ImGuiStyleVar.WindowPadding, new Vector2(plugin.Configuration.UiCompact ? 16 : 20) * scale);
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(plugin.Configuration.UiCompact ? 8 : 12, plugin.Configuration.UiCompact ? 6 : 10) * scale);
        style.Style(ImGuiStyleVar.FramePadding, new Vector2(plugin.Configuration.UiCompact ? 10 : 14, plugin.Configuration.UiCompact ? 4 : 7) * scale);
        style.Style(ImGuiStyleVar.CellPadding, new Vector2(plugin.Configuration.UiCompact ? 6 : 10, plugin.Configuration.UiCompact ? 4 : 8) * scale);
        style.Style(ImGuiStyleVar.FrameRounding, 4 * scale);
        style.Style(ImGuiStyleVar.ChildRounding, 4 * scale);
        using var body = plugin.Fonts.Push(UiFontRole.Body);
        using var chrome = MaterialWindowChrome.Push();
        plugin.WindowSystem.Draw();
        foreach (var window in plugin.WindowSystem.Windows)
            if (window.IsOpen)
            {
                WindowLayout.Title(window.WindowName, shapedText.Renderer);
                ApplyWindowOpacity(window.WindowName);
            }
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

    private bool DrawLanguage()
    {
        using var picker = plugin.Fonts.PushPicker(Ui.Language);
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(130 * scale, Ui.LanguageLabels.Max(label => MaterialText.Measure(label).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(width, width));
        var changed = false;
        if (MaterialText.BeginCombo("##ADSUiLanguage", Ui.LanguageLabels[(int)Ui.Language]))
        {
            try {
            for (var index = 0; index < Ui.LanguageLabels.Length; index++)
            {
                var language = (UiLanguage)index;
                if (MaterialText.Selectable($"{Ui.LanguageLabels[index]}##ADSUiLanguage{index}", language == Ui.Language))
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
        var compactVisible = plugin.Configuration.UiCompactVisibleOnMainWindow;
        if (WindowLayout.Checkbox("Compact visible on main window", ref compactVisible))
        { plugin.Configuration.UiCompactVisibleOnMainWindow = compactVisible; plugin.SaveConfiguration(); }
        var languageVisible = plugin.Configuration.UiLanguageVisibleOnMainWindow;
        if (WindowLayout.Checkbox("Language visible on main window", ref languageVisible))
        { plugin.Configuration.UiLanguageVisibleOnMainWindow = languageVisible; plugin.SaveConfiguration(); }
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
