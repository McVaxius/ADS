# Desynthesis

Open controls with `/ads desynth`.

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
- The Desynthesis window has Lumina-backed search for desynthable item add/remove actions.
- Raw JSON and UTF-8 JSON base64 import/export are supported.
- Invalid imports and duplicate names leave current data unchanged.
- Inventory context menus add/remove valid desynthable items from active preset.

## Last-Duty Ledger

Ledger lives only in local plugin config as `desynth-duty-ledger.json`, schema version `1`.

With configured source `LastDutyGains`, ADS snapshots regular inventory bags on duty entry. Completed duty exit finalizes positive deltas. Abandoned tracking remains visible until next tracked duty starts. Remaining quantity is consumed only after `SalvageResult` appears.

ADS does not begin new tracking after mid-duty reload. Persisted active tracking resumes.

## Runtime Checklist

1. Run `inventory-only`, `everywhere-skip-gearsets`, `everywhere`, `all`, `whitelist`, `last-duty`, and `skillups`; verify expected items.
2. Verify gearset items are skipped only by `InventoryAndArmourySkipGearsets`, and item `10146` is always skipped.
3. Verify context-menu add/remove persists immediately.
4. Verify invalid raw/base64 import leaves presets unchanged.
5. Complete and exit duty with `LastDutyGains`; verify gains and consumption.
