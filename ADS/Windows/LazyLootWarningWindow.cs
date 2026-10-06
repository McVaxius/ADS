using AethertekUI;
using ADS.Localization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class LazyLootWarningWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;

    public LazyLootWarningWindow(Plugin plugin)
        : base(
            "LazyLoot Warning###ADSLazyLootWarning",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 0f),
            MaximumSize = new Vector2(520f, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        WindowName = Ui.L("LazyLoot Warning###ADSLazyLootWarning");
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        => windowMotion.Restore(this);

    public override void Draw()
    {
        windowMotion.DrawChrome();
        MaterialText.TextWrapped(Ui.T("Are you sure you want to use LazyLoot? It can't recover hidden loot windows and only tries once."));
        ImGui.Spacing();

        if (WindowLayout.Button(Ui.L("Open /ads loot")))
        {
            plugin.OpenLootUi();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Don't show this message again")))
        {
            plugin.Configuration.LazyLootWarningDismissed = true;
            plugin.SaveConfiguration();
            IsOpen = false;
        }
    }
}
