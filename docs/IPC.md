# Stable IPC

Existing ADS IPC endpoints remain available.

## Endpoints

- `ADS.GetCapabilitiesJson() -> string`
- `ADS.Invoke(string action, string payloadJson) -> string`
- `ADS.GetConfigurationJson() -> string`
- `ADS.PatchConfigurationJson(string patchJson) -> string`
- `ADS.StartExtractMateria() -> bool`
- `ADS.GetExtractMateriaStatusJson() -> string`
- `ADS.StartDesynth(string mode) -> bool`
- `ADS.CancelUtility() -> bool`
- `ADS.OpenDesynthConfigUi() -> bool`
- `ADS.GetDesynthStatusJson() -> string`

`ADS.StartDesynth` accepts legacy modes `configured`, `all`, `whitelist`, `last-duty`, and `skillups`, plus direct source-scope aliases `inventory-only`, `everywhere-skip-gearsets`, and `everywhere`.

`ADS.StartExtractMateria` starts existing no-configuration materia extraction and returns whether the start was accepted. `/ads extractmateria` remains unchanged.

`ADS.GetExtractMateriaStatusJson` returns extraction-specific state:

```json
{"running":false,"done":true,"succeeded":true,"status":"Materia extraction finished.","success":"Materia extraction finished.","failure":"","completedAtUtc":"2026-06-10T12:34:56.7890000Z"}
```

- `done` becomes `true` only after an accepted extraction reaches success, failure, or cancellation.
- `succeeded` is `null` before completion, then `true` or `false`.
- Rejected starts and unrelated utility runs do not overwrite the last accepted extraction result.

Capability schema version is `1`. `ADS.Invoke` returns:

```json
{"success":true,"action":"utility.start-desynth","message":"Accepted.","data":null}
```

Unknown actions/settings, invalid payloads/modes, and invalid presets fail without mutation. Stable actions cover duty/window/utility operations, presets, ledger clear, and configuration patching. `utility.start-extract-materia` starts extraction without a payload.

Preferred desynthesis setting:

- `desynthInventoryScope`: `InventoryOnly`, `InventoryAndArmourySkipGearsets`, or `InventoryAndArmoury`.

Deprecated compatibility inputs:

- `desynthCategories`: accepted as raw legacy salvage category names, then normalized to `desynthInventoryScope`.
- `desynthProtectGearsets`: accepted as legacy input, then folded into `desynthInventoryScope`.
