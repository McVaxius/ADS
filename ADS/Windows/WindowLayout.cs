using ADS.Localization;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace ADS.Windows;

internal static class WindowLayout
{
    internal static bool PrepareMaterialBackdrop()
    {
        if (!AdsPresentation.IsMaterial) return false;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        return true;
    }

    internal static void RestoreMaterialBackdrop(ref bool pending)
    {
        if (!pending) return;
        ImGui.PopStyleColor();
        pending = false;
    }

    internal static bool BeginPopup(string id, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        bool open;
        using (var backdrop = MaterialSkin.BackdropStyle(popup: true))
            open = ImGui.BeginPopup(id, flags);
        if (open) MaterialSkin.Backdrop();
        return open;
    }

    internal static bool BeginCombo(string nativeLabel, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        bool open;
        using (var backdrop = MaterialSkin.BackdropStyle(popup: true))
        {
            var style = ImGui.GetStyle();
            var framePadding = style.FramePadding;
            var prepaintPadding = framePadding;
            if (AdsPresentation.IsMaterial)
            {
                var caption = nativeLabel.Split("##", 2)[0];
                var visiblePreview = (flags & ImGuiComboFlags.NoPreview) == 0 ? preview.Split("##", 2)[0] : "";
                // Query the native helper's renderer-aware padding without keeping its height scope active.
                using (var height = MaterialText.PushLineHeight(caption, visiblePreview))
                    prepaintPadding = style.FramePadding;
            }
            MaterialStyleScope field;
            try
            {
                // Paint at the shaped height; the native helper reserves that height itself.
                style.FramePadding = prepaintPadding;
                field = MaterialSkin.NativeFieldStyle();
            }
            finally { style.FramePadding = framePadding; }
            using (field)
                open = MaterialText.BeginCombo(nativeLabel, preview, flags);
        }
        if (open) MaterialSkin.Backdrop();
        return open;
    }

    internal static void SameLineIfFits(float nextWidth)
    {
        var windowRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + nextWidth <= windowRight)
            ImGui.SameLine();
    }

    // Existing translated ### labels retain their native IDs as whole controls move between rows.
    public static bool Checkbox(string label, ref bool value)
    {
        var text = Ui.Display(label.Split("##")[0]);
        MaterialLayout.FitNextItemWidth(0, MaterialText.Measure(text).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        return NativeCheckbox(Ui.L(label), ref value);
    }

    public static string InputLabel(string label, float minimum = 0)
    {
        var text = Ui.Display(label.Split("##")[0]);
        var requested = ImGui.CalcItemWidth();
        minimum = MathF.Ceiling(minimum > 0 ? minimum : TextMinimum(64));
        var labelWidth = MaterialText.Measure(text).X + ImGui.GetStyle().ItemInnerSpacing.X;
        MaterialLayout.FitNextItemWidth(requested + labelWidth, minimum + labelWidth);
        var available = ImGui.GetContentRegionAvail().X;
        if (minimum + labelWidth <= available)
        {
            ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(MathF.Min(requested, available - labelWidth), minimum));
            return Ui.L(label);
        }

        if (text.Length > 0) MaterialText.Text(text);
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(MathF.Min(requested, ImGui.GetContentRegionAvail().X), minimum));
        return "###" + label;
    }

    internal static float TextMinimum(int length) => MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale,
        MaterialText.Measure(new string('0', Math.Clamp(length - 1, 3, 10))).X + ImGui.GetStyle().FramePadding.X * 2));

    internal static float ComboMinimum(IEnumerable<string> previews) => MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale,
        previews.Select(preview => MaterialText.Measure(preview).X).DefaultIfEmpty(0).Max() + ImGui.GetFrameHeight() + 2 * ImGui.GetStyle().FramePadding.X));

    internal static float NumberMinimum(bool steps) => MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale,
        MaterialText.Measure("-000000").X + 2 * ImGui.GetStyle().FramePadding.X)) +
        (steps ? 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X) : 0);

    internal static float ButtonMinimum(string nativeLabel, MaterialIcon icon = MaterialIcon.None)
        => MaterialText.Measure(nativeLabel.Split("##", 2)[0]).X + 2 * ImGui.GetStyle().FramePadding.X +
           (icon == MaterialIcon.None ? 0 : 28 * MaterialTheme.Metrics.Scale);

    internal static unsafe bool Button(string nativeLabel, Vector2 size = default, MaterialIcon icon = MaterialIcon.None)
    {
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        using var outline = MaterialSkin.NativeButtonStyle();
        using var naturalHeight = MaterialText.PushLineHeight(nativeLabel.Split("##", 2)[0]);
        size.X = MaterialLayout.FitNextItemWidth(size.X, Math.Max(size.X, ButtonMinimum(nativeLabel, icon)));
        var caption = nativeLabel.Split("##", 2)[0];
        var inkOverflow = 0f;
        if (!MaterialText.RequiresShaping(caption))
        {
            var font = ImGui.GetFont(); var fontSize = ImGui.GetFontSize(); var fontScale = fontSize / font.FontSize;
            foreach (var character in caption)
            {
                var glyph = ImGui.FindGlyphNoFallback(font, character);
                if (glyph.Handle != null)
                    inkOverflow = Math.Max(inkOverflow, Math.Max(-glyph.Handle->Y0 * fontScale, glyph.Handle->Y1 * fontScale - fontSize));
            }
        }
        // Native centered text clips descenders that extend beyond the font's line box.
        size.Y = MathF.Ceiling(Math.Max(size.Y, Math.Max(ImGui.GetFrameHeight() + 2 * inkOverflow, icon == MaterialIcon.None ? 0 : 20 * MaterialTheme.Metrics.Scale)));
        if (!AdsPresentation.IsMaterial && icon == MaterialIcon.None && !MaterialText.RequiresShaping(nativeLabel.Split("##", 2)[0]))
        {
            var pressed = ImGui.Button(nativeLabel, size);
            MaterialLcars.Feedback(pressed);
            return pressed;
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool clicked;
        try
        {
            using var face = AdsPresentation.IsMaterial ? MaterialSkin.TransparentButtonFace() : default(MaterialStyleScope);
            clicked = ImGui.Button(nativeLabel, size);
        }
        finally { ImGui.PopStyleColor(); }
        var min = ImGui.GetItemRectMin();var max = ImGui.GetItemRectMax();var scale = MaterialTheme.Metrics.Scale;
        if (AdsPresentation.IsMaterial) MaterialSkin.PaintButton(min, max);
        var label = nativeLabel.Split("##", 2)[0];var text = MaterialText.Measure(label);var iconWidth = icon == MaterialIcon.None ? 0 : 28 * scale;var width = text.X + iconWidth;
        var x = min.X + Math.Max(ImGui.GetStyle().FramePadding.X, (max.X - min.X - width) * .5f);
        var color = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var dl = ImGui.GetWindowDrawList();dl.PushClipRect(min, max, true);
        try
        {
            if (icon != MaterialIcon.None) MaterialIcons.Draw(icon, new(x, min.Y + (max.Y - min.Y - 20 * scale) * .5f), 20 * scale, color, ImGui.GetStyle().Alpha);
            MaterialText.AddText(dl,new(x + iconWidth, min.Y + (max.Y - min.Y - text.Y) * .5f), ImGui.GetColorU32(ImGuiCol.Text), label);
        }
        finally { dl.PopClipRect(); }
        MaterialLcars.Feedback(clicked);
        return clicked;
    }

    internal static bool Header(string nativeLabel, MaterialIcon icon, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None, bool tight = false)
    {
        using var style = new MaterialStyleScope();
        style.Style(ImGuiStyleVar.FramePadding, new Vector2(AdsPresentation.Compact ? 6 : 10, AdsPresentation.Compact || tight ? 3 : 12) * MaterialTheme.Metrics.Scale);
        if (MaterialTheme.Current.IsLcars)
        {
            style.Color(ImGuiCol.Header, MaterialLcars.Lilac);
            style.Color(ImGuiCol.HeaderHovered, MaterialColor.Layer(MaterialLcars.Lilac, MaterialLcars.Text, .12f));
            style.Color(ImGuiCol.HeaderActive, MaterialLcars.Orange);
            style.Color(ImGuiCol.Text, MaterialLcars.Background);
        }
        else if (AdsPresentation.IsMaterial)
        {
            var colors = ImGui.GetStyle().Colors;
            style.Color(ImGuiCol.Button, colors[(int)ImGuiCol.Header]);
            style.Color(ImGuiCol.ButtonHovered, colors[(int)ImGuiCol.HeaderHovered]);
            style.Color(ImGuiCol.ButtonActive, colors[(int)ImGuiCol.HeaderActive]);
            style.Color(ImGuiCol.Header, Vector4.Zero);
            style.Color(ImGuiCol.HeaderHovered, Vector4.Zero);
            style.Color(ImGuiCol.HeaderActive, Vector4.Zero);
        }
        using var naturalHeight = MaterialText.PushLineHeight(nativeLabel.Split("##", 2)[0]);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);var open = ImGui.CollapsingHeader(nativeLabel, flags);ImGui.PopStyleColor();
        MaterialLcars.Feedback(ImGui.IsItemActivated(), MaterialUiSoundCue.Toggle);
        var min = ImGui.GetItemRectMin();var max = ImGui.GetItemRectMax();var scale = MaterialTheme.Metrics.Scale;
        if (AdsPresentation.IsMaterial) MaterialSkin.PaintButton(min, max);
        var label = nativeLabel.Split("##", 2)[0];var size = Math.Min(20 * scale, max.Y - min.Y);
        var color = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];var dl = ImGui.GetWindowDrawList();dl.PushClipRect(min, max, true);
        try
        {
            MaterialIcons.Draw(icon, min + new Vector2(8 * scale, (max.Y - min.Y - size) * .5f), size, color, ImGui.GetStyle().Alpha);
            MaterialText.AddText(dl,min + new Vector2(36 * scale, (max.Y-min.Y-MaterialText.Measure(label).Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), label);
            MaterialIcons.Draw(open ? MaterialIcon.ChevronDown : MaterialIcon.ArrowRight, new(max.X - 24 * scale, min.Y + (max.Y - min.Y - size) * .5f), size, color, ImGui.GetStyle().Alpha);
        }
        finally { dl.PopClipRect(); }
        return open;
    }

    internal static bool Tab(string nativeLabel, MaterialIcon icon)
    {
        var scale = MaterialTheme.Metrics.Scale;
        using var padding = new MaterialStyleScope();
        padding.Style(ImGuiStyleVar.FramePadding, new Vector2(AdsPresentation.Compact ? 22 : 42, AdsPresentation.Compact ? 3 : 12) * scale);
        if (AdsPresentation.IsMaterial)
        {
            padding.Color(ImGuiCol.Tab, Vector4.Zero);
            padding.Color(ImGuiCol.TabHovered, Vector4.Zero);
            padding.Color(ImGuiCol.TabActive, Vector4.Zero);
            padding.Color(ImGuiCol.TabUnfocused, Vector4.Zero);
            padding.Color(ImGuiCol.TabUnfocusedActive, Vector4.Zero);
        }
        using var naturalHeight = MaterialText.PushLineHeight(nativeLabel.Split("##", 2)[0]);
        ImGui.SetNextItemWidth(MaterialText.Measure(nativeLabel, true).X + 2 * ImGui.GetStyle().FramePadding.X);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var selected = ImGui.BeginTabItem(nativeLabel);
        MaterialLcars.Feedback(ImGui.IsItemActivated());
        ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        using var selectedStyle = new MaterialStyleScope();
        if (AdsPresentation.IsMaterial)
        {
            var colors = MaterialTheme.Current.Colors;
            var fill = selected ? colors.Primary : colors.SurfaceContainerHigh;
            selectedStyle.Color(ImGuiCol.Button, fill);
            selectedStyle.Color(ImGuiCol.ButtonHovered, Vector4.Lerp(fill, colors.OnSurface, .12f));
            selectedStyle.Color(ImGuiCol.ButtonActive, Vector4.Lerp(fill, colors.Background, .12f));
            if (selected) selectedStyle.Color(ImGuiCol.Text, colors.OnPrimary);
            MaterialSkin.PaintButton(min, max);
        }
        var label = nativeLabel.Split("##", 2)[0];
        var text = MaterialText.Measure(label);
        var x = min.X + (max.X - min.X - text.X - 32 * scale) * .5f;
        var color = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        try
        {
            MaterialIcons.Draw(icon, new(x, min.Y + (max.Y - min.Y - 20 * scale) * .5f), 20 * scale, color);
            MaterialText.AddText(dl,new(x + 32 * scale, min.Y + (max.Y - min.Y - text.Y) * .5f), ImGui.GetColorU32(ImGuiCol.Text), label);
        }
        catch { if(selected) ImGui.EndTabItem(); throw; }
        finally { dl.PopClipRect(); }
        return selected;
    }

    internal static bool NativeCheckbox(string nativeLabel, ref bool value)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        if (!MaterialText.RequiresShaping(caption))
        {
            var activated = ImGui.Checkbox(nativeLabel, ref value);
            MaterialLcars.Feedback(activated, MaterialUiSoundCue.Toggle);
            return activated;
        }
        using var height = MaterialText.PushLineHeight(caption);
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        bool changed;
        using(var style = new MaterialStyleScope())
        {
            style.Style(ImGuiStyleVar.ItemInnerSpacing, new Vector2(Math.Max(0, gap.X + MaterialText.Measure(caption).X - ImGui.CalcTextSize(caption).X), gap.Y));
            style.Color(ImGuiCol.Text, Vector4.Zero);
            changed = ImGui.Checkbox(nativeLabel, ref value);
        }
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.GetFrameHeight() + gap.X, (ImGui.GetItemRectMax().Y-ImGui.GetItemRectMin().Y-MaterialText.Measure(caption).Y)*.5f), color, caption);
        MaterialLcars.Feedback(changed, MaterialUiSoundCue.Toggle);
        return changed;
    }

    internal static bool SmallButton(string nativeLabel)
    {
        MaterialLayout.FitNextItemWidth(0, ButtonMinimum(nativeLabel));
        using var padding = new MaterialStyleScope();
        padding.Style(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0));
        return Button(nativeLabel);
    }

    internal static bool BeginTabItem(string nativeLabel, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        if (!MaterialText.RequiresShaping(caption))
        {
            var selected = ImGui.BeginTabItem(nativeLabel, flags);
            MaterialLcars.Feedback(ImGui.IsItemActivated());
            return selected;
        }
        using var height = MaterialText.PushLineHeight(caption);
        ImGui.SetNextItemWidth(MaterialText.Measure(caption).X + 2*ImGui.GetStyle().FramePadding.X);
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool open;
        try { open = ImGui.BeginTabItem(nativeLabel, flags); MaterialLcars.Feedback(ImGui.IsItemActivated()); }
        finally { ImGui.PopStyleColor(); }
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var drawing = ImGui.GetWindowDrawList();
        drawing.PushClipRect(min, max, true);
        try { MaterialText.AddText(drawing, min+(max-min-MaterialText.Measure(caption))*.5f, color, caption); }
        catch { if(open) ImGui.EndTabItem(); throw; }
        finally { drawing.PopClipRect(); }
        return open;
    }

    internal static bool Combo(string nativeLabel, ref int value, string[] options, int count)
    {
        var preview = value >= 0 && value < count ? options[value] : "";
        var changed = false;
        if (BeginCombo(nativeLabel, preview))
        {
            try
            {
                for(var index = 0; index < count; index++)
                {
                    ImGui.PushID(index);
                    try
                    {
                        if(MaterialText.Selectable(options[index], value == index)) { changed = true; value = index; }
                        if(value == index) ImGui.SetItemDefaultFocus();
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndCombo(); }
        }
        if(changed) ImGuiP.MarkItemEdited(ImGui.GetID(nativeLabel));
        MaterialLcars.Feedback(changed, MaterialUiSoundCue.Toggle);
        return changed;
    }

    internal static bool InputText(string nativeLabel, ref string value, int length, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
        => InputTextWithHint(nativeLabel, "", ref value, length, flags);

    internal static bool InputTextWithHint(string nativeLabel, string hint, ref string value, int length, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(caption, value, hint);
        using var field = MaterialSkin.NativeFieldStyle();
        if(!MaterialText.RequiresShaping(caption)) return MaterialShapedInput.SingleLine(nativeLabel, hint, ref value, length, flags);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.CalcItemWidth(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos;
        var drawing = ImGui.GetWindowDrawList();
        drawing.PushClipRect(new(origin.X, window.Pos.Y), new(origin.X+width, window.Pos.Y+window.Size.Y), true);
        bool changed;
        try { changed = MaterialShapedInput.SingleLine(nativeLabel, hint, ref value, length, flags); }
        finally { drawing.PopClipRect(); }
        var position = origin + new Vector2(width+ImGui.GetStyle().ItemInnerSpacing.X, (ImGui.GetFrameHeight()-MaterialText.Measure(caption).Y)*.5f);
        MaterialText.AddText(drawing, position, ImGui.GetColorU32(ImGuiCol.Text), caption);
        var right = position.X + MaterialText.Measure(caption).X;
        window.DC.CursorMaxPos.X = Math.Max(previousMax.X, right);
        window.DC.CursorPosPrevLine.X = right;
        return changed;
    }

    internal static bool InputTextMultiline(string nativeLabel, ref string value, int length, Vector2 size, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
        => MaterialShapedInput.Multiline(nativeLabel, ref value, length, size, flags);

    internal static bool InputInt(string nativeLabel, ref int value, int step = 0, int fastStep = 0, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(caption);
        using var field = MaterialSkin.NativeFieldStyle();
        if(!MaterialText.RequiresShaping(caption)) return ImGui.InputInt(nativeLabel, ref value, step, fastStep, flags: flags);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.CalcItemWidth(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var drawing = ImGui.GetWindowDrawList();
        drawing.PushClipRect(new(origin.X,window.Pos.Y),new(origin.X+width,window.Pos.Y+window.Size.Y),true);
        bool changed;
        try { changed = ImGui.InputInt(nativeLabel, ref value, step, fastStep, flags: flags); }
        finally { drawing.PopClipRect(); }
        NumericCaption(caption,origin,width,window,drawing,previousMax);
        return changed;
    }

    internal static bool InputFloat(string nativeLabel, ref float value, float step = 0, float fastStep = 0, string format = "%.3f", ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(caption);
        using var field = MaterialSkin.NativeFieldStyle();
        if(!MaterialText.RequiresShaping(caption)) return ImGui.InputFloat(nativeLabel, ref value, step, fastStep, format, flags);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.CalcItemWidth(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var drawing = ImGui.GetWindowDrawList();
        drawing.PushClipRect(new(origin.X,window.Pos.Y),new(origin.X+width,window.Pos.Y+window.Size.Y),true);
        bool changed;
        try { changed = ImGui.InputFloat(nativeLabel, ref value, step, fastStep, format, flags); }
        finally { drawing.PopClipRect(); }
        NumericCaption(caption,origin,width,window,drawing,previousMax);
        return changed;
    }

    private static void NumericCaption(string caption, Vector2 origin, float width, ImGuiWindowPtr window, ImDrawListPtr drawing, Vector2 previousMax)
    {
        var position = origin + new Vector2(width+ImGui.GetStyle().ItemInnerSpacing.X,(ImGui.GetFrameHeight()-MaterialText.Measure(caption).Y)*.5f);
        MaterialText.AddText(drawing,position,ImGui.GetColorU32(ImGuiCol.Text),caption);
        var right = position.X+MaterialText.Measure(caption).X;
        window.DC.CursorMaxPos.X = Math.Max(previousMax.X,right);
        window.DC.CursorPosPrevLine.X = right;
    }

    internal static bool RadioButton(string nativeLabel, ref int value, int buttonValue)
    {
        if(!RadioButton(nativeLabel, value == buttonValue)) return false;
        value = buttonValue; return true;
    }

    internal static bool RadioButton(string nativeLabel, bool selected)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        if(!MaterialText.RequiresShaping(caption))
        {
            var activated = ImGui.RadioButton(nativeLabel, selected);
            MaterialLcars.Feedback(activated, MaterialUiSoundCue.Toggle);
            return activated;
        }
        using var height = MaterialText.PushLineHeight(caption);
        var gap = ImGui.GetStyle().ItemInnerSpacing; var color = ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool changed;
        try { changed = ImGui.RadioButton(nativeLabel, selected); }
        finally { ImGui.PopStyleColor(); }
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        MaterialText.AddText(ImGui.GetWindowDrawList(), min + new Vector2(ImGui.GetFrameHeight()+gap.X, (max.Y-min.Y-MaterialText.Measure(caption).Y)*.5f), color, caption);
        MaterialLcars.Feedback(changed, MaterialUiSoundCue.Toggle);
        return changed;
    }

    internal static bool TreeNode(string nativeLabel)
        => MaterialText.TreeNode(nativeLabel);

    internal static float HeaderRowHeight()
        => Enumerable.Range(0, ImGui.TableGetColumnCount()).Select(index => MaterialText.Measure(ImGui.TableGetColumnName(index), true).Y).DefaultIfEmpty(ImGui.GetTextLineHeight()).Max()+2*ImGui.GetStyle().CellPadding.Y;

    internal static void TableHeader(string nativeLabel)
        => MaterialText.TableHeader(nativeLabel);

    internal static void PaintTitleWithImage(Dalamud.Interface.Windowing.Window owner)
        => PaintTitleWithImageAndStatus(owner, null);

    internal static void PaintTitleWithImageAndStatus(Dalamud.Interface.Windowing.Window owner, MaterialTitlebarStatus? status)
    {
        var window = ImGuiP.FindWindowByName(owner.WindowName);
        if (window.IsNull) return;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        var extraRightWidth = count * (ImGuiP.CalcFontSize(window) + ImGui.GetStyle().ItemInnerSpacing.X);
        var texture = AdsPresentation.OriginalIcon;
        var size = texture is null ? Vector2.Zero : new Vector2(texture.Width, texture.Height);
        if (status is { } summary)
            MaterialWindowHeader.PaintTitleWithStatus(window, owner.WindowName.Split("##", 2)[0], texture?.Handle ?? default,
                size, summary, extraRightWidth, owner.ShowCloseButton);
        else MaterialWindowHeader.PaintTitle(window, owner.WindowName.Split("##", 2)[0], texture?.Handle ?? default,
            size, extraRightWidth, owner.ShowCloseButton);
    }

    internal static void Title(string nativeLabel, MaterialTextRenderer renderer)
        => PaintTranslatedTitle(nativeLabel, renderer, null);

    internal static void TitleWithButtons(Dalamud.Interface.Windowing.IWindow owner, MaterialTextRenderer renderer)
        => PaintTranslatedTitle(owner.WindowName, renderer, owner as Dalamud.Interface.Windowing.Window);

    private static void PaintTranslatedTitle(string nativeLabel, MaterialTextRenderer renderer,
        Dalamud.Interface.Windowing.Window? owner)
    {
        var caption = nativeLabel.Split("##", 2)[0];
        if (!MaterialText.RequiresShaping(caption)) return;
        var window = ImGuiP.FindWindowByName(nativeLabel);
        if (window.IsNull || window.LastFrameActive != ImGui.GetFrameCount()
            || (window.Flags & ImGuiWindowFlags.NoTitleBar) != 0) return;
        var style = ImGui.GetStyle();
        var fontSize = ImGuiP.CalcFontSize(window);
        var titleHeight = ImGuiP.TitleBarHeight(window);
        var left = style.FramePadding.X;
        var right = style.FramePadding.X;
        var collapse = (window.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0;
        if (collapse && style.WindowMenuButtonPosition == ImGuiDir.Left)
            left += fontSize + style.ItemInnerSpacing.X;
        if (window.HasCloseButton) right += fontSize + style.ItemInnerSpacing.X;
        if (collapse && style.WindowMenuButtonPosition == ImGuiDir.Right)
            right += fontSize + style.ItemInnerSpacing.X;
        if (owner is not null)
        {
            var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
            if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
            right += count * (fontSize + style.ItemInnerSpacing.X);
        }
        var min = window.Pos + new Vector2(left, 0);
        var max = window.Pos + new Vector2(Math.Max(left, window.Size.X - right), titleHeight);
        var size = renderer.GetLayout(caption, fontSize).Size;
        var available = max - min;
        var position = min + new Vector2(Math.Max(0, available.X - size.X) * style.WindowTitleAlign.X,
            Math.Max(0, available.Y - size.Y) * style.WindowTitleAlign.Y);
        var navigation = ImGui.GetCurrentContext().NavWindow;
        var focused = !navigation.IsNull
            && window.RootWindowForTitleBarHighlight == navigation.RootWindowForTitleBarHighlight;
        var background = window.Collapsed ? ImGuiCol.TitleBgCollapsed : focused ? ImGuiCol.TitleBgActive : ImGuiCol.TitleBg;
        var drawing = window.DrawList;
        drawing.PushClipRect(min, max, false);
        try
        {
            drawing.AddRectFilled(min, max, ImGui.GetColorU32(background));
            MaterialText.AddText(drawing, ImGui.GetFont(), fontSize, position, ImGui.GetColorU32(ImGuiCol.Text), caption);
        }
        finally { drawing.PopClipRect(); }
    }

    internal static void TableHeadersRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, HeaderRowHeight());
        for(var index=0; index < ImGui.TableGetColumnCount(); index++)
            if(ImGui.TableSetColumnIndex(index)) TableHeader(ImGui.TableGetColumnName(index));
    }
}
