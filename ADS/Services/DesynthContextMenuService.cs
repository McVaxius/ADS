using ADS.Models;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DesynthContextMenuService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly DesynthPresetStore presetStore;
    private readonly IPluginLog log;

    public DesynthContextMenuService(
        IContextMenu contextMenu,
        IDataManager dataManager,
        Configuration configuration,
        DesynthPresetStore presetStore,
        IPluginLog log)
    {
        this.contextMenu = contextMenu;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.presetStore = presetStore;
        this.log = log;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
        => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!configuration.DesynthContextMenuEnabled
            || args.MenuType != ContextMenuType.Inventory
            || args.Target is not MenuTargetInventory inventoryTarget)
        {
            return;
        }

        if (inventoryTarget.TargetItem is not { } targetItem)
            return;

        var itemId = targetItem.BaseItemId;
        if (!IsValidDesynthableItem(itemId))
            return;

        var preset = presetStore.Get(configuration.DesynthActivePreset);
        var normalized = DesynthPolicyService.NormalizeBaseItemId(itemId);
        var contains = preset.ItemIds.Contains(normalized);
        args.AddMenuItem(new MenuItem
        {
            Name = $"{(contains ? "Remove from" : "Add to")} ADS {preset.Name}",
            OnClicked = _ =>
            {
                var success = contains
                    ? presetStore.RemoveItem(preset.Name, normalized, out var error)
                    : presetStore.AddItem(preset.Name, normalized, out error);
                if (!success)
                    log.Warning($"[ADS][Desynth] Context-menu preset update failed: {error}");
            },
        });
    }

    private bool IsValidDesynthableItem(uint itemId)
    {
        var sheet = dataManager.GetExcelSheet<Item>();
        return sheet != null
               && sheet.TryGetRow(DesynthPolicyService.NormalizeBaseItemId(itemId), out var item)
               && item.Desynth > 0;
    }
}
