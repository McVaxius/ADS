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
    private bool materialBackdropPending;
    private readonly AethertekUI.Dalamud.MaterialWindowLock windowLock = new();

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
        windowMotion.Prepare(this, reducedMotion: !plugin.Configuration.UiStylingEnabled, roundedCorners: plugin.Configuration.UiStylingEnabled);
        materialBackdropPending = WindowLayout.PrepareMaterialBackdrop(plugin.Appearance.BackgroundRequested);
        windowLock.Apply(this, plugin.Configuration.UiWindowsLocked);
    }

    public override void PostDraw()
    {
        windowLock.Restore(this);
        WindowLayout.RestoreMaterialBackdrop(ref materialBackdropPending);
        windowMotion.Restore(this);
    }

    public override void Draw()
    {
        WindowLayout.RestoreMaterialBackdrop(ref materialBackdropPending);
        windowMotion.DrawChrome();
        plugin.Appearance.DrawBackdrop(this);
        using var structuralBackground = MaterialSkin.StructuralBackgroundStyle();
        if (MaterialTheme.Current.IsLcars) MaterialLcars.Header(Ui.T("LazyLoot Warning"));
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
