# Desynthesis

Open controls with `/ads desynth`.

The window separates run, policy, preset, and last-duty ledger controls. Choose one run mode, select **Start**, or select **Stop Desynthesis** while a desynthesis run is active.

## Run Modes

- `configured`: configured run filter, source scope, skill-up filter, and active preset.
- `all`: all items allowed by configured source scope and skill-up filter.
- `whitelist`: active preset items within configured source scope and skill-up filter.
- `last-duty`: remaining positive gains from the last completed tracked duty within configured source scope.
- `skillups`: all items in configured source scope that match the configured skill-up threshold.
- `inventory-only`: direct run for inventory equipment only.
- `everywhere-skip-gearsets`: direct run for inventory and armoury, with gearset protection on.
- `everywhere`: direct run for inventory and armoury, with gearset protection off.

All modes exclude item `10146`. Skill-up eligibility matches AutoDuty: current desynthesis level must be below item level plus threshold and below maximum available desynthesis level.

## Source Scopes

ADS exposes three source scopes:

- `InventoryOnly`: inventory equipment only.
- `InventoryAndArmourySkipGearsets`: inventory equipment plus armoury categories, excluding gearset items.
- `InventoryAndArmoury`: inventory equipment plus armoury categories, including gearset items.

Legacy raw salvage categories such as `InventoryHousing`, `Equipped`, and armoury bucket names are accepted only as deprecated configuration input and are normalized into one of these scopes.

## Presets

Presets live only in local plugin config as `desynth-presets.json`, schema version `1`.

- `DEFAULT` cannot be renamed or deleted.
- IDs normalize to distinct base item IDs.
- The Desynthesis window has Lumina-backed search, **Add Selected Item**, and scrollable active-preset rows with per-item **Remove** actions.
- **Copy Presets** writes formatted JSON to the clipboard.
- **Import Presets** reads the clipboard, tries JSON first, then legacy UTF-8 JSON base64.
- Empty/invalid clipboard imports and duplicate names leave current data unchanged.
- The persisted `Description` field remains accepted and preserved for legacy preset compatibility, but is no longer edited in the window.
- Inventory context menus add/remove valid desynthable items from active preset.

## Last-Duty Ledger

Ledger lives only in local plugin config as `desynth-duty-ledger.json`, schema version `1`.

With configured source `LastDutyGains`, ADS snapshots regular inventory bags on duty entry. Completed duty exit finalizes positive deltas. Abandoned tracking remains visible until next tracked duty starts. Remaining quantity is consumed only after `SalvageResult` appears.

ADS does not begin new tracking after mid-duty reload. Persisted active tracking resumes.

## Runtime Checklist

1. Run `inventory-only`, `everywhere-skip-gearsets`, `everywhere`, `all`, `whitelist`, `last-duty`, and `skillups`; verify expected items.
2. Verify gearset items are skipped only by `InventoryAndArmourySkipGearsets`, and item `10146` is always skipped.
3. Verify selected-item add, per-row remove, and context-menu add/remove update immediately and survive reload.
4. Verify JSON/base64 clipboard import, legacy descriptions, and invalid/empty import atomicity.
5. Complete and exit duty with `LastDutyGains`; verify gains and consumption.
