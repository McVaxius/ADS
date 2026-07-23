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
- `ADS.StartShopPurchase(uint itemId, int quantity) -> bool`
- `ADS.GetShopPurchaseStatusJson() -> string`
- `ADS.CancelUtility() -> bool`
- `ADS.OpenDesynthConfigUi() -> bool`
- `ADS.IsDutyOwned() -> bool`
- `ADS.GetDesynthStatusJson() -> string`

`ADS.IsDutyOwned` is authoritative runtime duty ownership. It returns `true` only while live instanced-duty truth is active and ADS mode is `OwnedStartOutside`, `OwnedStartInside`, `OwnedResumeInside`, or `Leaving`. It returns `false` for `Idle`, `Observing`, `Failed`, and queued outside-duty ownership.

`ADS.StartDesynth` accepts legacy modes `configured`, `all`, `whitelist`, `last-duty`, and `skillups`, plus direct source-scope aliases `inventory-only`, `everywhere-skip-gearsets`, and `everywhere`.

`ADS.StartExtractMateria` starts existing no-configuration materia extraction and returns whether the start was accepted. `/ads extractmateria` remains unchanged.

`ADS.StartShopPurchase` accepts a positive decimal `uint` item ID and a quantity from `1` through `9999`. `true` means ADS accepted the run; it does not mean the purchase completed. Quantity is the exact number of additional item units. `ADS.CancelUtility` cancels an active purchase and preserves verified partial acquisition truth.

The generic equivalent is:

```text
ADS.Invoke("utility.start-shop-purchase", "{\"itemId\":38420,\"quantity\":3}")
```

The action is additive and capability schema version remains `1`.

`ADS.GetExtractMateriaStatusJson` returns extraction-specific state:

```json
{"running":false,"done":true,"succeeded":true,"status":"Materia extraction finished.","success":"Materia extraction finished.","failure":"","completedAtUtc":"2026-06-10T12:34:56.7890000Z"}
```

- `done` becomes `true` only after an accepted extraction reaches success, failure, or cancellation.
- `succeeded` is `null` before completion, then `true` or `false`.
- Rejected starts and unrelated utility runs do not overwrite the last accepted extraction result.

`ADS.GetShopPurchaseStatusJson` returns purchase-specific state. Offer currency amounts are totals required for the request and the amount available when offers were evaluated.

```json
{
  "running": false,
  "done": true,
  "succeeded": true,
  "phase": "completed",
  "itemId": 38420,
  "itemName": "Manderium Meteorite",
  "requestedQuantity": 3,
  "acquiredQuantity": 3,
  "remainingQuantity": 0,
  "selectedOffer": {
    "shopKind": "special-shop-tomestone",
    "shopId": 1770624,
    "shopName": "Manderville Weapon Materials Exchange",
    "row": 0,
    "npcId": 1043892,
    "npcName": "Jubrunnah",
    "territoryId": 963,
    "territoryName": "Radz-at-Han",
    "receiveCount": 1,
    "currencies": [
      {
        "kind": "tomestone",
        "itemId": 28,
        "name": "Allagan Tomestones of Poetics",
        "requiredAmount": 1500,
        "availableAmount": 2000,
        "availabilityKnown": true
      }
    ],
    "outputs": [
      {
        "itemId": 38420,
        "name": "Manderium Meteorite",
        "countPerTransaction": 1
      }
    ],
    "availabilityKnown": true
  },
  "alternativeOffers": [],
  "failureCode": null,
  "statusMessage": "Purchased exactly 3 additional Manderium Meteorite.",
  "successMessage": "Purchased exactly 3 additional Manderium Meteorite.",
  "failureMessage": "",
  "lastStartError": "",
  "completedAtUtc": "2026-07-14T12:34:56.7890000Z"
}
```

The additive `shopKind` values are `special-shop-mixed`, `inclusion-shop`, `grand-company-shop`, and `free-company-shop`. Additive currency kinds include `company-seal`, `mgp`, `wolf-mark`, `allied-seal`, `currency-manager`, and `free-company-credit`. `outputs` describes every deterministic output per transaction. `availabilityKnown` is false when a sheet gate or balance must be proven in the live shop, such as Free Company credits.

The stable failure-code vocabulary is `invalid-request`, `busy`, `unsupported-offer`, `no-route`, `insufficient-currency`, `ambiguous-currency`, `missing-dependency`, `inventory-capacity`, `ui-mismatch`, `timeout`, and `cancelled`. Accepted runs place an applicable terminal code in `failureCode`. Rejected starts preserve the prior `failureCode` and update only `lastStartError`; they do not replace the last accepted purchase result. Unrelated utility runs also leave that result intact. Capability schema version remains `1`.

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
