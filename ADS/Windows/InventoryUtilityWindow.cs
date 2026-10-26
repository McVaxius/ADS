using System.Numerics;
using ADS.Localization;
using ADS.Models;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class InventoryUtilityWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly (string Label, string Help, MaterialIcon Icon, Action Run, bool StartsWork)[] gear;
    private readonly (string Label, string Help, MaterialIcon Icon, Action Run, bool StartsWork)[] selling;
    private readonly (string Label, string Help, MaterialIcon Icon, Action Run, bool StartsWork)[] tools;
    private (int BagsFree, int ArmouryFree, int BagGear, int ArmouryGear)? summary;
    private DateTime lastCompletion;
    private ulong summaryCharacter;

    public InventoryUtilityWindow(Plugin plugin) : base("ADS Inventory###ADSInventoryUtilities")
    {
        this.plugin = plugin;
        Size = new Vector2(396, plugin.Configuration.UiCompact ? 480 : 620);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(320, 300), MaximumSize = new(1200, 1600) };
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
        gear =
        [
            ("Auto Equip", "Equip recommended gear for the current job and update its active gearset.",
                MaterialIcon.Play, () => plugin.StartAutoEquip(), true),
            ("Move white gear", "Move armoury gear to inventory; protect saved gearsets and stop when bags fill.",
                MaterialIcon.Backpack, () => plugin.StartGearMove(), true),
            ("Move all unprotected", "Move armoury gear to inventory; protect saved gearsets and stop when bags fill.",
                MaterialIcon.Backpack, () => plugin.StartGearMove(allUnprotected: true), true),
        ];
        selling =
        [
            ("Sell white: inventory", "Preview and sell eligible white equipment; protect saved gearsets.",
                MaterialIcon.Cart, () => plugin.PreviewGearSale(GearCleanupScope.Inventory), true),
            ("Sell white: armoury", "Preview and sell eligible white equipment; protect saved gearsets.",
                MaterialIcon.Cart, () => plugin.PreviewGearSale(GearCleanupScope.Armoury), true),
            ("Sell white: both", "Preview and sell eligible white equipment; protect saved gearsets.",
                MaterialIcon.Cart, () => plugin.PreviewGearSale(GearCleanupScope.Both), true),
            ("Sell AR list", "Sell AutoRetainer's configured list, separate from white-equipment selling.",
                MaterialIcon.Cart, () => plugin.StartConfiguredNpcSale(), true),
        ];
        tools =
        [
            ("Self repair", "", MaterialIcon.Wrench, () => plugin.StartSelfRepair(), true),
            ("NPC repair", "", MaterialIcon.Wrench, () => plugin.StartNpcRepair(), true),
            ("Extract Materia", "",
                MaterialIcon.Wrench, () => plugin.StartExtractMateria(), true),
            ("Desynth Controls", "Open controls without starting work.",
                MaterialIcon.List, plugin.OpenDesynthConfigUi, false),
            ("Shop Lists", "Open controls without starting work.", MaterialIcon.Cart, plugin.OpenShopListsUi, false),
        ];
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Home, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenMainUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(Ui.T("Main")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Redo, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) Refresh(); },
            ShowTooltip = () => MaterialText.SetTooltip(Ui.T("Refresh inventory counts")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Stop, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.CancelUtility(); },
            ShowTooltip = () => MaterialText.SetTooltip(Ui.T("Stop inventory utility")),
        });
    }

    public void Dispose() { }
    public override void OnOpen() => Refresh();

    public override void PreDraw()
    {
        PrepareWindowPlacement();
        ReserveTitleSpace(320);
        PrepareWindowChrome();
    }

    private void Refresh()
    {
        summary = plugin.UtilityAutomationService.ReadInventorySummary();
        lastCompletion = plugin.UtilityAutomationService.LastCompletionUtc;
        summaryCharacter = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
    }

    public override void Draw()
    {
        DrawWindowChrome();
        FinalizePendingWindowPlacement();
        var character = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
        if (lastCompletion != plugin.UtilityAutomationService.LastCompletionUtc || summaryCharacter != character) Refresh();
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        if (summary is { } counts)
        {
            MaterialText.Text(Ui.T("Free slots: bags {0} / armoury {1}", counts.BagsFree, counts.ArmouryFree));
            MaterialText.TextDisabled(Ui.T("White gear: bags {0} / armoury {1}", counts.BagGear, counts.ArmouryGear));
        }
        else MaterialText.TextDisabled(Ui.T("Inventory counts unavailable"));
        MaterialText.TextWrapped(Ui.Display(plugin.UtilityAutomationService.StatusMessage));
        ImGui.BeginDisabled(!plugin.UtilityAutomationService.IsRunning);
        try
        {
            if (WindowLayout.Button(Ui.L("Stop inventory utility"), new Vector2(-1, 0), MaterialIcon.Stop)) plugin.CancelUtility();
        }
        finally { ImGui.EndDisabled(); }
        DrawActions("Gear", gear);
        DrawActions("Selling", selling);
        DrawActions("Tools", tools);
    }

    private void DrawActions(string heading,
        (string Label, string Help, MaterialIcon Icon, Action Run, bool StartsWork)[] actions)
    {
        MaterialSettings.Section(Ui.T(heading));
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        var minimum = actions.Max(action => WindowLayout.ButtonMinimum(Ui.L(action.Label), action.Icon)) + padding;
        var columns = ImGui.GetContentRegionAvail().X >= 2 * minimum ? 2 : 1;
        using var rows = MaterialTable.PushTightRows();
        if (!ImGui.BeginTable("ADSInventory_" + heading, columns, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            foreach (var action in actions)
            {
                ImGui.TableNextColumn();
                var blocker = action.StartsWork ? plugin.InventoryUtilityBlocker : null;
                ImGui.BeginDisabled(blocker != null);
                try
                {
                    if (WindowLayout.Button(Ui.L(action.Label), new Vector2(-1, 0), action.Icon)) action.Run();
                    if ((action.Help.Length > 0 || blocker != null) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        MaterialText.SetTooltip(Ui.T(action.Help) + (blocker == null ? string.Empty : "\n" + Ui.Display(blocker)));
                }
                finally { ImGui.EndDisabled(); }
            }
        }
        finally { ImGui.EndTable(); }
    }
}
