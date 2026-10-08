using System.Numerics;
using AethertekUI;
using ADS.Localization;
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
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        => windowMotion.Restore(this);

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
        var imageWidth = this is MainWindow or QuickControlWindow ? fontSize + style.ItemInnerSpacing.X : 0;
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
