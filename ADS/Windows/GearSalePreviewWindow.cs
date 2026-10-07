using System.Numerics;
using AethertekUI;
using ADS.Localization;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ADS.Windows;

public sealed class GearSalePreviewWindow(Plugin plugin) : PositionedWindow("Equipment sale preview###ADSGearSalePreview"), IDisposable
{
    private GearCleanupSelection? selection;

    public void Show(GearCleanupSelection value)
    {
        selection = value;
        Size = new Vector2(660, 500);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 300), MaximumSize = new Vector2(1400, 1200) };
        IsOpen = true;
    }

    public void CloseSelection() { selection = null; IsOpen = false; }
    public void Dispose() => CloseSelection();
    public override void OnClose() => selection = null;

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();
        var current = selection;
        if (current == null) { IsOpen = false; return; }
        MaterialText.TextWrapped(Ui.T("Sell {0} white-name equipment items from {1}?", current.Items.Count, Ui.Display(current.Scope.ToString())));
        var confirm = plugin.Configuration.GearSaleConfirmationEnabled;
        if (WindowLayout.Checkbox("Preview equipment before selling", ref confirm))
        {
            plugin.Configuration.GearSaleConfirmationEnabled = confirm;
            plugin.SaveConfiguration();
        }
        if (WindowLayout.Button(Ui.L("Sell###ADSGearSaleConfirm")))
        {
            plugin.StartConfirmedGearSale(current);
            CloseSelection();
        }
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Cancel###ADSGearSaleCancel"))) CloseSelection();
        ImGui.Separator();
        if (ImGui.BeginChild("ADSGearSaleItems", Vector2.Zero))
        {
            foreach (var item in current.Items)
            {
                var name = item.Name + (((item.Item.Flags & InventoryItem.ItemFlags.HighQuality) != 0) ? " HQ" : string.Empty);
                MaterialText.TextWrapped(Ui.T("{0} - {1}, slot {2}", name, Ui.Display(SourceLabel(item.Container)), item.Slot + 1));
            }
        }
        ImGui.EndChild();
    }

    private static string SourceLabel(InventoryType type) => type switch
    {
        InventoryType.Inventory1 => "Inventory bag 1", InventoryType.Inventory2 => "Inventory bag 2",
        InventoryType.Inventory3 => "Inventory bag 3", InventoryType.Inventory4 => "Inventory bag 4",
        InventoryType.ArmoryMainHand => "Armoury main hand", InventoryType.ArmoryOffHand => "Armoury off hand",
        InventoryType.ArmoryHead => "Armoury head", InventoryType.ArmoryBody => "Armoury body",
        InventoryType.ArmoryHands => "Armoury hands", InventoryType.ArmoryLegs => "Armoury legs",
        InventoryType.ArmoryFeets => "Armoury feet", InventoryType.ArmoryEar => "Armoury earrings",
        InventoryType.ArmoryNeck => "Armoury necklace", InventoryType.ArmoryWrist => "Armoury bracelets",
        InventoryType.ArmoryRings => "Armoury rings", _ => string.Empty,
    };
}
