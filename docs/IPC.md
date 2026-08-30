# Stable IPC

Existing ADS IPC endpoints remain available.

## Endpoints

- `ADS.StartDutyFromOutside() -> bool`
- `ADS.StartDutyFromInside() -> bool`
- `ADS.ResumeDutyFromInside() -> bool`
- `ADS.LeaveDuty() -> bool`
- `ADS.GetCapabilitiesJson() -> string`
- `ADS.Invoke(string action, string payloadJson) -> string`
- `ADS.GetConfigurationJson() -> string`
- `ADS.PatchConfigurationJson(string patchJson) -> string`
- `ADS.StartExtractMateria() -> bool`
- `ADS.GetExtractMateriaStatusJson() -> string`
- `ADS.StartDesynth(string mode) -> bool`
- `ADS.StartShopPurchase(uint itemId, int quantity) -> bool`
- `ADS.SetShopKeepOpen(bool enabled) -> bool`
- `ADS.GetShopPurchaseStatusJson() -> string`
- `ADS.GetShopListPresetsJson() -> string`
- `ADS.PreviewShopListPreset(string requestJson) -> string`
- `ADS.StartShopListPreset(string requestJson) -> string`
- `ADS.GetShopListPresetStatusJson(string operationId) -> string`
- `ADS.CancelShopListPreset(string operationId) -> bool`
- `ADS.SearchShopCatalogJson(string requestJson) -> string`
- `ADS.CancelUtility() -> bool`
- `ADS.OpenDesynthConfigUi() -> bool`
- `ADS.IsDutyOwned() -> bool`
- `ADS.GetDesynthStatusJson() -> string`

`ADS.StartDutyFromOutside` and `ADS.StartDutyFromInside` share the same start methods as ADS chat, UI, and operator actions. They best-effort-send `/xldisableplugin AutoDuty` before ownership work; a dispatch failure is logged and the start still proceeds. `ADS.ResumeDutyFromInside` does not disable AutoDuty.

`ADS.IsDutyOwned` is authoritative runtime duty ownership. It returns `true` only while live instanced-duty truth is active and ADS mode is `OwnedStartOutside`, `OwnedStartInside`, `OwnedResumeInside`, or `Leaving`. It returns `false` for `Idle`, `Observing`, `Failed`, and queued outside-duty ownership.

`ADS.StartDesynth` accepts legacy modes `configured`, `all`, `whitelist`, `last-duty`, and `skillups`, plus direct source-scope aliases `inventory-only`, `everywhere-skip-gearsets`, and `everywhere`.

`ADS.StartExtractMateria` starts existing no-configuration materia extraction and returns whether the start was accepted. `/ads extractmateria` remains unchanged.

`ADS.StartShopPurchase` accepts a positive decimal `uint` item ID and a quantity from `1` through `9999`. `true` means ADS accepted the run; it does not mean the purchase completed. Quantity is the exact number of additional item units. `ADS.CancelUtility` cancels an active purchase and preserves verified partial acquisition truth.

`ADS.SetShopKeepOpen` toggles shop reuse across consecutive purchases and returns the value now in effect. While on, a SUCCESSFUL purchase leaves its shop open so the next purchase from the same shop skips navigate/interact/open; a failed run still tears the UI down. Calling it with `false` ends the chain AND closes whatever was left standing ? that is the supported way to finish, because `ADS.CancelUtility` cannot close a held shop: every cancel path early-returns unless a purchase is still running, and a held shop only exists once the purchase is terminal.

Turning it on is what makes a multi-item run from one vendor work reliably: repeated testing showed that close-and-re-interact cycles can leave the character in an unfinished NPC event, causing later interactions to be silently ignored.

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

## Shop List Presets

All Shop List JSON uses camelCase schema version `1`. `ADS.GetShopListPresetsJson()` returns rename-safe preset identities:

```json
{"version":1,"activePresetId":"00000000-0000-0000-0000-000000000001","presets":[{"presetId":"00000000-0000-0000-0000-000000000001","name":"Poetics","mode":"targeted-refill","currencyKind":"tomestone","currencyItemId":28,"currencyThreshold":1500,"rowCount":2}]}
```

`ADS.PreviewShopListPreset` is strictly purchase-free. It may read live inventory and current-character XA Database retainers, but it never starts travel, opens a shop, or spends currency:

```json
{"version":1,"presetId":"00000000-0000-0000-0000-000000000001","completedRowIds":[]}
```

The response contains `version`, `presetId`, `disposition`, `currencyAvailable`, `completedNonRepeatableRowIds`, `message`, and stored-order `rows`. Dispositions are `ready`, `not-triggered`, `fulfilled`, or `error`. Each preview row contains `rowId`, `itemId`, `itemName`, `triggerBelow`, `refillToAtLeast`, `repeatable`, `ownershipScope`, `liveInventoryQuantity`, `retainerQuantity`, `ownedQuantity`, `purchaseQuantity`, `outcome`, retainer evidence, `selectedOffer`, `failureCode`, and `statusMessage`. Spend preview assigns live currency/capacity only to the first eligible stored-order row and reports later rows as `deferred`.

Start is an atomic re-evaluate-and-start operation:

```text
ADS.StartShopListPreset("{\"version\":1,\"operationId\":\"dad-plan-42-run-7\",\"presetId\":\"00000000-0000-0000-0000-000000000001\",\"completedRowIds\":[]}")
```

It returns:

```json
{"version":1,"accepted":true,"operationId":"dad-plan-42-run-7","presetId":"00000000-0000-0000-0000-000000000001","disposition":"started","completedNonRepeatableRowIds":[],"message":"Starting shop-list batch with 2 row(s)."}
```

`accepted:false` with `not-triggered` or `fulfilled` is a successful purchase-free disposition, not an execution failure. ADS retains a correlation-bound terminal status for both, with `done:true`, `succeeded:true`, and the exact subset of non-repeatable rows already proven complete. `not-triggered` can leave rows pending, and a spend-limit result can leave capacity/currency-skipped non-repeatable rows incomplete. Consumers must merge only `completedNonRepeatableRowIds`, never infer row completion from generic success or `completedRows`. Stale removed/rotated completion IDs are ignored; repeatable IDs are never skipped. An `error` disposition does not start or retain an operation.

Poll only the exact operation:

```text
ADS.GetShopListPresetStatusJson("dad-plan-42-run-7")
```

The response contains `version`, `operationId`, `presetId`, `running`, `done`, `succeeded`, `disposition`, row counts/current item, `completedNonRepeatableRowIds`, association-supplied `skippedRowIds`, `failureCode`, messages, `completedAtUtc`, and stored-order rows. Operation dispositions are `running`, `not-triggered`, `fulfilled`, `succeeded`, `failed`, or `cancelled` (`operation-not-found` is returned for a mismatched correlation ID):

```json
{"rowId":"00000000-0000-0000-0000-000000000002","itemId":123,"itemName":"Example","repeatable":false,"ownershipScope":"inventory-and-retainers","triggerBelow":10,"refillToAtLeast":20,"ownedQuantity":4,"requestedQuantity":16,"purchasedQuantity":16,"outcome":"purchased","message":"Verified purchase acquired 16 item(s)."}
```

Row outcomes are `association-completed`, `already-satisfied`, `pending`, `running`, `purchased`, `deferred`, `skipped`, `failed`, or `cancelled`. Dad should merge `completedNonRepeatableRowIds` into only the exact Plan/Schedule association; ADS never globally completes a preset row or deletes Dad objects. `ADS.CancelShopListPreset(operationId)` cancels only a matching active operation and returns `false` for a mismatch or terminal/no-op operation.

Catalog search accepts item/vendor/NPC/territory/currency text. Supply an exact currency identity to filter, or omit `currencyKind` with `currencyItemId:0` to discover identities:

```json
{"version":1,"query":"Radz-at-Han","currencyKind":"tomestone","currencyItemId":28,"limit":50}
```

Rows return item/bundle, shop kind/ID/row/name, NPC ID/name, territory ID/name, numeric `x`/`y`/`z`, `copyableXyz`, and exact single-currency kind/item/name/cost. Search is local and never purchases.

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
