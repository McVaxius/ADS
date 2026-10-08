using System.Numerics;
using ADS.Localization;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace ADS.Windows;

internal enum UiFontRole { Body, BodyStrong, Title, PluginName, Counter, Action, CompactTitle, Caption, CompactAction }

internal static class AdsPresentation
{
    // Dalamud owns the shared texture through render submission; callers borrow its wrapper.
    internal static Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? OriginalIcon
        => Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, "ADS.images.icon.png").GetWrapOrDefault();

    internal static void DrawPluginIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        var texture = OriginalIcon;
        if (texture is not null)
            MaterialCanvas.DrawImage(drawList, texture.Handle, new Vector2(texture.Width, texture.Height), min, max);
    }

    // Approved ADS-review-v2 and ADS-compact-review-v1, measured in logical pixels.
    internal const uint ReferenceAccent = 0x0067FF;
    internal static readonly float[] FontSizes = [14, 16, 32, 20, 18, 16, 30, 12, 14];
    internal static readonly string[] FontFiles = ["segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "seguisb.ttf", "seguisb.ttf", "seguisb.ttf", "segoeuib.ttf", "segoeui.ttf", "seguisb.ttf"];
    internal static float AtlasHeight(UiFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal static bool Compact => MaterialTheme.Current.Density == MaterialDensity.Compact;
    internal static float HeaderHeight => Compact ? 62 : 68;
    internal static float Gap => Compact ? 10 : 14;
    internal static float ControlHeight => Compact ? 54 : 66;
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var selected = Rgb(accent);
        var reference = Rgb(ReferenceAccent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var original = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(reference.X, reference.Y, reference.Z)));
        var hue = seed.Y < .001f ? 0 : seed.Z - original.Z;
        var chroma = seed.Y < .001f ? 0 : seed.Y / original.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chroma, lch.Z + hue), 1);
        }
        var background = Relative(0x0C1A26);
        var foreground = Relative(0xF2F4F9);
        var primary = Relative(ReferenceAccent);
        var colors = new MaterialColorScheme(new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z)))
        {
            Background = background, OnBackground = foreground,
            Surface = Relative(0x10202D), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x0D1C28), SurfaceContainerLow = Relative(0x10212E),
            SurfaceContainer = Relative(0x142634), SurfaceContainerHigh = Relative(0x182B3B), SurfaceContainerHighest = Relative(0x203344),
            SurfaceVariant = Relative(0x233B4D), OnSurfaceVariant = Relative(0xBCD5ED),
            Outline = Relative(0x4A647A), OutlineVariant = Relative(0x304C60),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x0047AF), OnPrimaryContainer = foreground,
            Secondary = Relative(0xBCD5ED), OnSecondary = background, SecondaryContainer = Relative(0x203344), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xB3CAE4), OnTertiary = background, TertiaryContainer = Relative(0x21354C), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0x004CC1),
        };
        return new(colors, MaterialDensity.Standard) { SurfaceOpacity = 1 };
    }

    internal static MaterialControlMetrics Controls(float height = 28)
    {
        var s = MaterialTheme.Metrics.Scale;
        return new() { Height = height * s, Padding = new(10 * s, Math.Max(0, (height * s - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * s, IconSize = 20 * s, Rounding = 4 * s, ItemSpacing = new(8 * s, 6 * s), CellPadding = new(12 * s, 6 * s) };
    }

    internal static void Panel(System.Action draw, uint originalIdRoot, float minimumHeight = 0)
        => Surface(draw, originalIdRoot, minimumHeight,
            new Vector2(Compact ? 14 : 20, Compact ? 12 : 16), false);

    internal static void Inset(System.Action draw, uint originalIdRoot)
        => Inset(draw, originalIdRoot, 0);

    internal static void Inset(System.Action draw, uint originalIdRoot, float minimumHeight)
        => Surface(draw, originalIdRoot, minimumHeight, new Vector2(12, 8), true);

    internal static void ToolPanel(System.Action draw, uint originalIdRoot)
        => Surface(draw, originalIdRoot, 0, new Vector2(6), true);

    private static void Surface(System.Action draw, uint originalIdRoot, float minimumHeight, Vector2 logicalPadding, bool inner)
    {
        var s = MaterialTheme.Metrics.Scale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var padding = logicalPadding * s;
        var dl = ImGui.GetWindowDrawList();
        var splitter = ImGui.ImDrawListSplitter();
        splitter.Split(dl, 2);
        try
        {
            splitter.SetCurrentChannel(dl, 1);
            ImGui.SetCursorScreenPos(start + padding);
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1, width - padding.X * 2));
            ImGui.PushItemWidth(Math.Max(1, width - padding.X * 2));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Vector2.Zero);
            var visible = ImGui.BeginTable(inner ? "##ADSInsetBounds" : "##ADSPanelBounds", 1,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(Math.Max(1, width - padding.X * 2), 0));
            ImGui.PopStyleVar();
            try
            {
                if (visible)
                {
                    ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, Math.Max(1, width - padding.X * 2));
                    ImGui.TableNextColumn();
                    // Bounds may change; existing controls retain their prior native ID root.
                    ImGuiP.PushOverrideID(originalIdRoot);
                    try { draw(); }
                    finally { ImGui.PopID(); }
                }
            }
            finally
            {
                if (visible) ImGui.EndTable();
                ImGui.PopItemWidth();
                ImGui.PopTextWrapPos();
                ImGui.EndGroup();
            }
            var bottom = Math.Max(start.Y + minimumHeight * s, ImGui.GetItemRectMax().Y + padding.Y);
            splitter.SetCurrentChannel(dl, 0);
            var c = MaterialTheme.Current.Colors;
            MaterialCanvas.Surface(start, new(start.X + width, bottom), inner ? c.SurfaceContainerLowest : c.SurfaceContainerHigh,
                inner ? c.SurfaceContainerLow : c.Surface, 5 * s);
            dl.AddRect(start, new(start.X + width, bottom), MaterialCanvas.Color(c.OutlineVariant), 5 * s);
            ImGui.SetCursorScreenPos(new(start.X, bottom));
            ImGui.Dummy(new(width, 0));
        }
        finally
        {
            splitter.Merge(dl);
            splitter.Destroy();
        }
    }

    internal static void QuickStatus(Func<UiFontRole, IDisposable> pushFont, string headline, string status)
    {
        using var style = new MaterialStyleScope();
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * MaterialTheme.Metrics.Scale);
        Surface(() =>
        {
            using (pushFont(UiFontRole.BodyStrong)) MaterialText.TextWrapped(headline);
            using (pushFont(UiFontRole.Body)) MaterialText.TextWrapped(status);
        }, ImGui.GetID(""), Compact ? 54 : 64, new Vector2(12, Compact ? 6 : 8), true);
    }

    internal static bool Action(UiFonts fonts, string label, string caption, string id, Vector4? fill = null)
        => Action(fonts.Push, label, caption, id, fill);

    internal static float ActionMinimum(Func<UiFontRole, IDisposable> pushFont, string label, string caption)
    {
        float titleWidth;float captionWidth;
        using (pushFont(Compact ? UiFontRole.CompactAction : UiFontRole.Action)) titleWidth = MaterialText.Measure(label).X;
        using (pushFont(UiFontRole.Caption)) captionWidth = MaterialText.Measure(caption).X;
        return Math.Max(titleWidth, captionWidth) + (Compact ? 66 : 72) * MaterialTheme.Metrics.Scale;
    }

    internal static bool Action(Func<UiFontRole, IDisposable> pushFont, string label, string caption, string id, Vector4? fill = null)
        => ActionCore(pushFont, label, caption, id, fill, ControlHeight);

    internal static bool QuickAction(UiFonts fonts, string label, string caption, string id, Vector4? fill = null)
        => ActionCore(fonts.Push, label, caption, id, fill, Compact ? 50 : 60);

    private static bool ActionCore(Func<UiFontRole, IDisposable> pushFont, string label, string caption, string id, Vector4? fill, float height)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var colors = MaterialTheme.Current.Colors;
        using var style = new MaterialStyleScope();
        style.Color(ImGuiCol.Button, fill ?? colors.SurfaceContainerHighest);
        style.Color(ImGuiCol.ButtonHovered, fill is null ? colors.SurfaceVariant : Vector4.Lerp(fill.Value, colors.OnSurface, .12f));
        style.Color(ImGuiCol.ButtonActive, fill is null ? colors.SurfaceContainerHigh : Vector4.Lerp(fill.Value, colors.Background, .12f));
        style.Color(ImGuiCol.Border, colors.OutlineVariant);
        style.Style(ImGuiStyleVar.FrameBorderSize, 1 * scale);
        var width = MaterialLayout.FitNextItemWidth(-1f, ActionMinimum(pushFont, label, caption));
        var verticalShift = (ControlHeight - height) * scale * .5f;
        var titleTop = (Compact ? 10 : 14) * scale - verticalShift;
        var captionTop = (Compact ? 30 : 38) * scale - verticalShift;
        var buttonHeight = height * scale;
        if(MaterialText.RequiresShaping(label) || MaterialText.RequiresShaping(caption))
        {
            Vector2 titleSize, captionSize; float nativeTitleHeight, nativeCaptionHeight;
            using(pushFont(Compact ? UiFontRole.CompactAction : UiFontRole.Action))
            { titleSize = MaterialText.Measure(label); nativeTitleHeight = ImGui.GetTextLineHeight(); }
            using(pushFont(UiFontRole.Caption))
            { captionSize = MaterialText.Measure(caption); nativeCaptionHeight = ImGui.GetTextLineHeight(); }
            var bottomGap = Math.Max(0,buttonHeight-captionTop-nativeCaptionHeight);
            captionTop = Math.Max(captionTop,titleTop+titleSize.Y+Math.Max(0,captionTop-titleTop-nativeTitleHeight));
            buttonHeight = Math.Max(buttonHeight,captionTop+captionSize.Y+bottomGap);
        }
        var clicked = ImGui.Button("###" + id, new Vector2(width, buttonHeight));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var foreground = fill is null ? colors.OnSurface : MaterialColor.Contrast(fill.Value, colors.Background) > MaterialColor.Contrast(fill.Value, colors.OnSurface) ? colors.Background : colors.OnSurface;
        var icon = id switch { "Start Outside" or "Start Inside" => MaterialIcon.Play, "Resume" => MaterialIcon.Refresh,
            "Leave" => MaterialIcon.DoorExit, "Stop" => MaterialIcon.Stop, _ => MaterialIcon.Wrench };
        var dl = ImGui.GetWindowDrawList();
        var clippedText = false;
        dl.PushClipRect(min, max, true);
        try
        {
            var iconSize = (Compact ? 26 : 32) * scale;
            MaterialIcons.Draw(icon, min + new Vector2(14 * scale, (max.Y - min.Y - iconSize) / 2), iconSize, foreground);
            var textX = min.X + iconSize + 26 * scale;
            using (pushFont(Compact ? UiFontRole.CompactAction : UiFontRole.Action))
            {
                clippedText = MaterialText.Measure(label).X > max.X - textX - 14 * scale;
                MaterialText.AddText(dl,new Vector2(textX, min.Y + titleTop), MaterialCanvas.Color(foreground), label);
            }
            using (pushFont(UiFontRole.Caption))
            {
                clippedText |= MaterialText.Measure(caption).X > max.X - textX - 14 * scale;
                MaterialText.AddText(dl,new Vector2(textX, min.Y + captionTop), MaterialCanvas.Color(foreground), caption);
            }
        }
        finally { dl.PopClipRect(); }
        if (clippedText && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            MaterialText.SetTooltip($"{label}\n{caption}");
        return clicked;
    }

    internal static void Summary(MaterialIcon icon, string caption, string value)
        => Summary(null, icon, caption, value);

    internal static void Summary(Func<UiFontRole, IDisposable>? pushFont, MaterialIcon icon, string caption, string value)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var size = (Compact ? 30 : 34) * scale;
        using var style = new MaterialStyleScope();
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * scale);
        Surface(() =>
        {
            var min = ImGui.GetCursorScreenPos();
            MaterialIcons.Draw(icon, min + new Vector2(0, 2 * scale), size, MaterialTheme.Current.Colors.OnSurfaceVariant);
            ImGui.Dummy(new Vector2(size));
            ImGui.SameLine(0, 14 * scale);
            ImGui.BeginGroup();
            using (pushFont?.Invoke(UiFontRole.Caption))
                MaterialText.TextColored(MaterialTheme.Current.Colors.OnSurfaceVariant, caption);
            using (pushFont?.Invoke(UiFontRole.BodyStrong))
                MaterialText.TextWrapped(value);
            ImGui.EndGroup();
        }, ImGui.GetID(""), Compact ? 56 : 68, new Vector2(12, Compact ? 8 : 10), true);
    }

    internal static void Heading(Func<UiFontRole, IDisposable> pushFont, MaterialIcon icon, string title, string description)
    {
        using var style = new MaterialStyleScope();
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * MaterialTheme.Metrics.Scale);
        var scale = MaterialTheme.Metrics.Scale;var size = 32 * scale;var min = ImGui.GetCursorScreenPos();
        ImGui.BeginGroup();MaterialIcons.Draw(icon, min, size, MaterialTheme.Current.Colors.OnSurfaceVariant);
        ImGui.Dummy(new Vector2(size));ImGui.SameLine(0, (Compact ? 20 : 16) * scale);ImGui.BeginGroup();
        using (pushFont(UiFontRole.BodyStrong)) MaterialText.Text(title);
        ImGui.PushStyleColor(ImGuiCol.Text, MaterialTheme.Current.Colors.OnSurfaceVariant);
        MaterialText.TextWrapped(description);ImGui.PopStyleColor();ImGui.EndGroup();ImGui.EndGroup();
    }

    internal static void NoOwnershipWarning(string text)
        => NoOwnershipWarning(null, text);

    internal static void NoOwnershipWarning(Func<UiFontRole, IDisposable>? pushFont, string text)
    {
        using var font = pushFont?.Invoke(UiFontRole.BodyStrong);
        var scale = MaterialTheme.Metrics.Scale;var size = (Compact ? 38 : 44) * scale;var gap = 16 * scale;
        var width = size + gap + MaterialText.Measure(text).X;
        ImGui.Dummy(new Vector2(0, (Compact ? 38 : 48) * scale));
        var min = ImGui.GetCursorScreenPos() + new Vector2(Math.Max(0, (ImGui.GetContentRegionAvail().X - width) * .5f), 0);
        ImGui.SetCursorScreenPos(min);var color = MaterialTheme.Current.Colors.OnSurfaceVariant;
        ImGui.GetWindowDrawList().AddCircle(min + new Vector2(size * .5f), size * .46f, MaterialCanvas.Color(color), 32, 2 * scale);
        MaterialIcons.Draw(MaterialIcon.Check, min + new Vector2(size * .22f), size * .56f, color);
        ImGui.Dummy(new Vector2(size));ImGui.SameLine(0, gap);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, (size - ImGui.GetTextLineHeight()) * .5f));MaterialText.Text(text);
    }
}
