# Sheet-Driven Shop Purchasing

Start a purchase with:

```text
/ads shop <itemID> <quantity>
```

`itemID` is a positive decimal `uint`. `quantity` is `1` through `9999` and means the exact number of additional item units ADS should acquire. It is not a transaction count or a target inventory total. ADS rejects malformed, missing, extra, non-decimal, zero, and out-of-range arguments without starting automation.

## Supported Offers

ADS supports deterministic acquisition offers from:

- regular `GilShop` offers priced by the requested item's vendor price;
- direct and `FateShop`-routed `SpecialShop` exchanges;
- `InclusionShop` category/series routes to `SpecialShop`;
- `GCShop` seal exchanges;
- `FccShop` Free Company credit exchanges.

The requested quantity must divide exactly by the offer's receive count. ADS does not round quantities or overbuy. A deterministic non-HQ multi-output exchange is supported only when every coproduct has capacity and every output delta can be verified exactly. One-to-three mixed currency costs are normalized through physical items, `TomestonesItem`, the audited virtual-currency map, company seals, MGP, Wolf Marks, Allied Seals, and `CurrencyManager`.

High-quality target rows, collectability costs, overbuying, unresolved currency codes, and ambiguous currency choices fail closed. `CollectablesShop` and `DisposalShop` are turn-in/sale surfaces; `LotteryExchangeShop` is nondeterministic. They return `unsupported-offer` without travel or callbacks.

## Discovery And Selection

ADS builds a lazy local catalog from installed Lumina sheets and game files. It recursively resolves every supported terminal path with cycle detection:

- direct `ENpcBase` to `GilShop` or `SpecialShop`;
- `TopicSelect` to a supported shop;
- direct `PreHandler` to a supported shop;
- `TopicSelect` to `PreHandler` to a supported shop;
- `CustomTalk.SpecialLinks` and unambiguous typed script arguments;
- `FateShop` to `SpecialShop`;
- `InclusionShop` to category, series, and `SpecialShop`;
- `GCShop` and `FccShop`.

Sheet and event-handler `LocalIndex` values remain diagnostic. At every visible selection step, ADS matches the expected handler ID against the selected NPC's live event-handler options, requires exactly one live match, validates its `GlobalIndex` against the live options array, and sends that global callback index. Hidden/dynamic and nested entries therefore remap safely, and ADS never probes adjacent indexes.

Teleport routes join each real `Aetheryte` to its exact `Map` and the map's exact aetheryte `MapMarker`; marker X/Y values are converted back to world X/Z with the map size factor and offsets. A map/territory mismatch, missing marker, or duplicate matching marker is never guessed.

Those mapped aetheryte territories drive live NPC placement discovery. ADS derives each territory's `bg/.../level/planevent.lgb` path from `TerritoryType.Bg`, reads every `EventNPC` base ID and full X/Y/Z world position through Lumina `LgbFile`, then merges `Level` placements. If neither source contains a linked NPC, ADS uses the embedded `shop-npc-placements.json` fallback generated from the offline Garland NPC browse database. Explicitly marked correction rows may replace known-invalid LGB/`Level` placements; ordinary offline rows never displace live game data. The correction input preserves the established missing-location rows from [ItemVendorLocation](https://github.com/electr0sheep/ItemVendorLocation/blob/main/ItemVendorLocation/ItemLookup.Fix.cs).

Placements for the same NPC and territory within one world unit are deduplicated with LGB preferred; genuinely distinct positions remain separate candidates. Filtering to supported-shop NPCs happens only after all sources are collected. Every matching shop row, NPC link, and distinct placement becomes a candidate even when candidates share a shop ID.

ADS logs mapped/rejected/missing aetherytes; every LGB territory and file outcome; LGB, `Level`, offline-fallback, and replacement counts; deduplication; all same-item candidates; rejection reasons; selected route; and fallback progression. Production does not call Garland, XIVAPI, or another vendor-data network service.

Before travel, ADS checks completed quests, Inclusion unlock quests, current Grand Company/rank, unlocked routes, known balances, and capacity for every output. A known-denied gate or known-insufficient balance is rejected. Achievement, festival, content, quest-state, FC-rank, and unavailable FC-credit truth is deferred to live UI proof. Known-affordable offers outrank deferred-balance offers. `GilShopItem.StateRequired` is ignored when no quest is attached, avoiding the false Gysahl Greens and Grade 8 Dark Matter gate.

Unaffordable and unreachable offers are ignored. If affordable offers spend different currency identities, ADS returns `ambiguous-currency`. Within one currency set, a component-wise cheaper offer wins; incomparable costs remain ambiguous. Exact-cost ties prefer the current territory, no teleport, the shortest unlocked-aetheryte route, then stable shop and NPC IDs.

Fallback is limited to offers for the exact requested item and quantity with the same currency identities and exact total cost, and can occur only before any purchase callback has been sent. Each candidate's own receive bundle must still produce the requested quantity exactly and validate live. ADS never substitutes another item, silently changes currency, or accepts a more expensive offer. Floor resolution, travel, NPC presence, interaction, menu opening, and live validation failures may advance to the next ranked candidate. Owned navigation must be confirmed stopped and owned shop UI is closed before switching. If validation fails for every candidate, the terminal result is `ui-mismatch` with zero callbacks for invalid candidates.

## Offline Catalog Regeneration

`tools/generate_shop_npc_placements.py` is an offline-only generator. It enumerates direct, TopicSelect, PreHandler, FATE, Inclusion, Grand Company, Free Company, and recursive CustomTalk carrier links from a local xivdatamine `csv/en` tree, joins linked NPCs to a local Garland `browse/en/2/npc` JSON snapshot, converts map coordinates to world X/Z, and merges `tools/shop-npc-location-corrections.json`. It writes the sorted embedded catalog and `docs/shop-npc-placement-audit.json`; every missing and ambiguous map join stays visible instead of being guessed.

```text
python tools/generate_shop_npc_placements.py --csv-root <xivdatamine>/csv/en --garland-npcs <offline-garland>/npc.json
python tools/generate_shop_npc_placements.py --csv-root <xivdatamine>/csv/en --garland-npcs <offline-garland>/npc.json --check
python -m unittest discover -s tools/tests -p "test_*.py" -v
```

## Runtime Safety

vnavmesh is required to approach the resolved NPC. Lifestream is required only for a selected cross-territory route, and ADS requires an unlocked aetheryte. An accepted teleport command is sent once per attempted candidate and monitored for up to 90 seconds without resending.

Offline catalog rows intentionally contain X/Z only. After ADS enters the selected territory, it first uses a visible live NPC's full X/Y/Z position. Otherwise it asks `vnavmesh.Query.Mesh.PointOnFloor` for the floor at the catalog X/Z and waits up to 20 seconds before falling back. ADS sends one `/vnav moveto` command and, once accepted, monitors NPC distance for up to 120 seconds without resending. If the live NPC appears materially away from the approximate catalog destination, ADS stops its owned movement and retargets once to the live full position. A rejected movement command send is retried at most three times.

Owned movement cleanup invokes `vnavmesh.Path.Stop` and requires `vnavmesh.Path.IsRunning == false`. `/vnav stop` is a compatibility fallback only; sending it without a readable `Path.IsRunning` result is not treated as success. The `stopping-navigation` phase makes at most three stop attempts two seconds apart. ADS does not interact, select a menu, issue replacement movement, or advance to another candidate until the owned path is confirmed stopped. Three `StillRunning` or `Unverified` results terminate as `no-route` without an NPC or purchase callback; terminal cleanup makes one final best-effort stop and records an unverified result. A shop, selection window, or confirmation dialog appearing unexpectedly during navigation produces `ui-mismatch` and no callback.

Before every purchase callback, ADS validates the runtime surface owned by that shop adapter:

- regular `Shop` uses [`ShopEventHandler`](https://github.com/aers/FFXIVClientStructs/blob/d25004c582d2c5d78118830d79ffd1479fe650ee/FFXIVClientStructs/FFXIV/Client/Game/Event/ShopEventHandler.cs) and requires the exact active `GilShop` ID plus one unique visible non-HQ item with the expected item ID and exact gil price; its visible index is the callback row;
- direct special/FATE exchanges use [`AgentShop`](https://github.com/aers/FFXIVClientStructs/blob/d25004c582d2c5d78118830d79ffd1479fe650ee/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentShop.cs) and require the exact sheet shop name plus one unique receive row and its complete one-to-three-entry cost span;
- `InclusionShop` selects the sheet page/subpage, reads the typed `AddonInclusionShop` values, and requires a unique item, bundle, and exact-cost row;
- `GrandCompanyExchange` selects the sheet-derived rank/category tabs and requires exact live item, seal price, company, and required rank;
- `FreeCompanyCreditShop` requires exact item, price, FC rank, credits, and maximum quantity.

A wrong shop, item, price, HQ state, duplicate row, malformed runtime layout, receive bundle, currency identity, or cost never receives a callback. Before the first callback, that candidate is closed and the next identical-cost candidate may be tried. Waiting states and diagnostics identify the unavailable `ShopEventHandler` or `AgentShop` surface; exhausting validation candidates produces `ui-mismatch`.

Callbacks use the validated addon only:

- `Shop`: `(0, row, batch)`;
- `ShopExchangeItem`: `(0, row, batch)`;
- `ShopExchangeCurrency`: `(0, row, batch, 0)`.
- `InclusionShop`: `(12, page)`, `(13, subpage)`, then `(14, row, batch)`;
- `GrandCompanyExchange`: the validated row and exact bounded quantity;
- `FreeCompanyCreditShop`: `(0, row, batch)`.

Each callback is capped at `99` transactions. ADS then uses one shared ten-second confirmation/verification window for an owned prompt, every exact output increase, and every exact currency decrease. It never blindly resends a callback. A missing delta times out; a contradictory delta returns `ui-mismatch`. Verified inventory, coproduct capacity, currency, and live UI are checked again before another batch. A requested/output item or tracked-currency change outside ADS's verified callback window, cancellation, or any failure after a callback is immediate and never triggers vendor fallback.

An immediate confirmation created by ADS's validated callback receives a single-use owned token containing the expected item, quantity, and every currency amount. A readable structured dialog or localized prompt that matches exactly is accepted once through ten seconds. An expired, duplicate, or readable mismatched confirmation remains `ui-mismatch`; an unreadable owned prompt times out without resending the purchase callback or advancing to a fallback.

Travel is bounded to 90 seconds, offline floor resolution to 20 seconds, navigation to 120 seconds, shop opening to 20 seconds, relevant command retries to three, and the whole run to five minutes.

## Live Acceptance

After rebuilding and reloading Debug ADS, choose any inexpensive supported item and start from another territory with vnavmesh and Lifestream loaded. The user must trigger this because it spends character currency.

```text
/ads shop <itemID> 1
```

Confirm the log enumerates every same-item candidate with placement source and callback path, attempts one teleport for the chosen candidate, sends one persistent movement command, stops navigation before UI work, validates the exact live shop row, sends one callback, and observes the exact requested-item and currency deltas.

Versatile Lure remains a secondary regression because its teleport-accessible Limsa merchant (NPC `1005422`) is absent from `Level` and `planevent.lgb` placement discovery and is supplied by the generic offline correction catalog:

```text
/ads shop 29717 1
```

It is not a production special case and is not the sole acceptance item. Gysahl Greens (`/ads shop 4868 1`) at Maisenta and Grade 8 Dark Matter (`/ads shop 33916 1`) at Alaric are the regular-gil state-gate/live-row regressions. Any wrong active shop ID, visible item, HQ state, price, duplicate row, or malformed handler layout must stay callback-free.

## Exclusivity, Cancellation, And Status

Shop purchasing is an exclusive ADS utility. A start is rejected while ADS owns duty execution, inn entry or another utility is active, the player is unavailable, or a required navigation dependency is missing. Starts never replace active automation.

Use `/ads desynth stop`, `ADS.CancelUtility()`, or `/ads stop` to cancel. Logout, disposal, unexpected zoning, ownership changes, cancellation, and timeouts halt future callbacks, stop owned navigation, close owned shop addons, and preserve the observed partial acquisition count.

IPC callers can use `ADS.StartShopPurchase(uint itemId, int quantity)`, `ADS.GetShopPurchaseStatusJson()`, or generic action `utility.start-shop-purchase`. See [IPC](IPC.md) for the status schema and stable failure codes.
