using ADS.Localization;
using Dalamud.Bindings.ImGui;

namespace ADS.Windows;

internal static class WindowLayout
{
    // Keep long translated captions beside the native checkbox without clipping.
    public static bool Checkbox(string label, ref bool value)
    {
        var text = Ui.Display(label.Split("##")[0]);
        if (ImGui.CalcTextSize(text).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X <= ImGui.GetContentRegionAvail().X)
            return ImGui.Checkbox(Ui.L(label), ref value);

        var changed = ImGui.Checkbox("###" + label, ref value);
        ImGui.SameLine();
        ImGui.TextWrapped(text);
        return changed;
    }

    public static string InputLabel(string label)
    {
        var text = Ui.Display(label.Split("##")[0]);
        var available = ImGui.GetContentRegionAvail().X;
        var labelWidth = ImGui.CalcTextSize(text).X + ImGui.GetStyle().ItemInnerSpacing.X;
        if (labelWidth < available * 0.5f)
        {
            ImGui.SetNextItemWidth(MathF.Min(ImGui.CalcItemWidth(), available - labelWidth));
            return Ui.L(label);
        }

        ImGui.TextWrapped(text);
        ImGui.SetNextItemWidth(available);
        return "###" + label;
    }
}
