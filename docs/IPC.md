# Stable IPC

Existing ADS IPC endpoints remain available.

## Endpoints

- `ADS.GetCapabilitiesJson() -> string`
- `ADS.Invoke(string action, string payloadJson) -> string`
- `ADS.GetConfigurationJson() -> string`
- `ADS.PatchConfigurationJson(string patchJson) -> string`
- `ADS.StartDesynth(string mode) -> bool`
- `ADS.CancelUtility() -> bool`
- `ADS.OpenDesynthConfigUi() -> bool`
- `ADS.GetDesynthStatusJson() -> string`

`ADS.StartDesynth` accepts legacy modes `configured`, `all`, `whitelist`, `last-duty`, and `skillups`, plus direct source-scope aliases `inventory-only`, `everywhere-skip-gearsets`, and `everywhere`.

Capability schema version is `1`. `ADS.Invoke` returns:

```json
{"success":true,"action":"utility.start-desynth","message":"Accepted.","data":null}
```

Unknown actions/settings, invalid payloads/modes, and invalid presets fail without mutation. Stable actions cover duty/window/utility operations, presets, ledger clear, and configuration patching.

Preferred desynthesis setting:

- `desynthInventoryScope`: `InventoryOnly`, `InventoryAndArmourySkipGearsets`, or `InventoryAndArmoury`.

Deprecated compatibility inputs:

- `desynthCategories`: accepted as raw legacy salvage category names, then normalized to `desynthInventoryScope`.
- `desynthProtectGearsets`: accepted as legacy input, then folded into `desynthInventoryScope`.
