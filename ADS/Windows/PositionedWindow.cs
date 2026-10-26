using System.Numerics;
using AethertekUI;
using ADS.Localization;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public abstract class PositionedWindow : Window
{
    protected readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly string originalWindowName;
    private Vector2? pendingWindowPosition;
    private bool pendingPositionConditionReset;
    private bool materialBackdropPending;

    protected PositionedWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        : base(name, flags)
    {
        originalWindowName = name;
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Flags &= ~ImGuiWindowFlags.NoResize;
    }

    public void QueueResetToOrigin()
        => QueueWindowPosition(new Vector2(1f, 1f));

    public void QueueRandomVisibleJump()
        => QueueWindowPosition(GetRandomVisiblePosition());

    public override void PreDraw()
    {
        PrepareWindowPlacement();
        PrepareWindowChrome();
    }

    protected void PrepareWindowChrome()
    {
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
        materialBackdropPending = WindowLayout.PrepareMaterialBackdrop();
    }

    public override void PostDraw()
    {
        WindowLayout.RestoreMaterialBackdrop(ref materialBackdropPending);
        windowMotion.Restore(this);
    }

    protected void DrawWindowChrome()
    {
        WindowLayout.RestoreMaterialBackdrop(ref materialBackdropPending);
        windowMotion.DrawChrome();
        MaterialSkin.Backdrop();
        if (MaterialTheme.Current.IsLcars && (Flags & ImGuiWindowFlags.NoTitleBar) == 0)
            MaterialLcars.Header(this is MainWindow ? string.Empty : WindowName.Split("##", 2)[0]);
    }

    protected void PrepareWindowPlacement()
    {
        WindowName = Ui.L(originalWindowName);
        if (!pendingWindowPosition.HasValue)
            return;

        Position = pendingWindowPosition.Value;
        PositionCondition = ImGuiCond.Always;
        pendingWindowPosition = null;
        pendingPositionConditionReset = true;
    }

    protected void ReserveTitleSpace(float minimumWidth)
    {
        var style = ImGui.GetStyle();
        var fontSize = ImGui.GetFontSize();
        var count = TitleBarButtons.Count(button => !IsClickthrough || button.AvailableClickthrough);
        if (AllowPinning || AllowClickthrough || AllowBackgroundBlur) count++;
        if (ShowCloseButton) count++;
        if ((Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0
            && style.WindowMenuButtonPosition != ImGuiDir.None) count++;
        var imageWidth = this is MainWindow or QuickControlWindow or InventoryUtilityWindow ? fontSize + style.ItemInnerSpacing.X : 0;
        var required = (MaterialText.Measure(WindowName.Split("##", 2)[0]).X + imageWidth
            + count * (fontSize + style.ItemInnerSpacing.X) + style.FramePadding.X * 2 + style.ItemInnerSpacing.X)
            / ImGui.GetIO().FontGlobalScale;
        var bounds = SizeConstraints ?? new WindowSizeConstraints();
        bounds.MinimumSize = new(Math.Max(minimumWidth, required), bounds.MinimumSize.Y);
        SizeConstraints = bounds;
    }

    protected void FinalizePendingWindowPlacement()
    {
        if (!pendingPositionConditionReset)
            return;

        pendingPositionConditionReset = false;
        Position = null;
        PositionCondition = ImGuiCond.None;
    }

    private protected static void CloseToolGuideOffer(Configuration configuration, Action save, ToolGuide tool, ref bool requested, ref bool visible)
    {
        if (visible && WizardCatalog.ConsumeToolGuideOffer(configuration, tool))
            save();
        requested = visible = false;
    }

    private protected static void DrawToolGuideOffer(Configuration configuration, Action save, ToolGuide tool, ref bool requested, ref bool visible, Action openGuide)
    {
        if (!WizardCatalog.ShouldOfferToolGuide(configuration, tool))
        {
            requested = visible = false;
            return;
        }
        var title = Ui.L(tool == ToolGuide.Rules
            ? "Rules guide###ADSRuleGuideOffer" : "Object Explorer guide###ADSExplorerGuideOffer");
        if (requested)
        {
            requested = false;
            visible = true;
        }
        if (!visible)
            return;

        var open = true;
        var launch = false;
        bool drawContent;
        using (var backdrop = MaterialSkin.BackdropStyle())
            drawContent = ImGui.Begin(title, ref open, ImGuiWindowFlags.AlwaysAutoResize);
        try
        {
            if (drawContent)
            {
                MaterialSkin.Backdrop();
                if (MaterialTheme.Current.IsLcars)
                    MaterialLcars.Header(Ui.T(tool == ToolGuide.Rules ? "Rules guide" : "Object Explorer guide"));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 440 * MaterialTheme.Metrics.Scale);
                MaterialText.TextWrapped(Ui.T("Open this tool's guide? You can always use its Guide button later."));
                ImGui.PopTextWrapPos();
                if (WindowLayout.Button(Ui.L("Open guide")))
                {
                    launch = true;
                    open = false;
                }
                ImGui.SameLine();
                if (WindowLayout.Button(Ui.L("Dismiss")))
                    open = false;
            }
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape))
                open = false;
        }
        finally { ImGui.End(); }
        if (!open)
            CloseToolGuideOffer(configuration, save, tool, ref requested, ref visible);
        if (launch)
            openGuide();
    }

    private void QueueWindowPosition(Vector2 position)
        => pendingWindowPosition = position;

    private Vector2 GetRandomVisiblePosition()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var currentSize = Size ?? Vector2.Zero;
        var minimumSize = SizeConstraints?.MinimumSize ?? Vector2.Zero;
        var width = MathF.Max(currentSize.X, minimumSize.X);
        var height = MathF.Max(currentSize.Y, minimumSize.Y);
        var maxX = MathF.Max(1f, viewport.Size.X - width - 20f);
        var maxY = MathF.Max(1f, viewport.Size.Y - height - 20f);
        return new Vector2(1f + (Random.Shared.NextSingle() * maxX), 1f + (Random.Shared.NextSingle() * maxY));
    }
}
