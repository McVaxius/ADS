# Changelog

## 2026-08-20

- Added a startup warning when LazyLoot is loaded, with actions to open `/ads loot` or permanently dismiss the message. Existing loot automation and ADS's owned-duty LazyLoot disable behavior are unchanged.
- Added persisted Shop Lists, opened with `/ads shopper`, compact Controls, or Main > Tools. TeamCraft, Crafting as a Service, and Artisan clipboard imports resolve locally; live inventory and current-character XA Database retainer holdings reduce desired totals; previewed purchases run sequentially through the existing fail-closed shop runner with same-shop reuse and immediate stop/cancel behavior. Existing `/ads shop` and IPC contracts are unchanged.
- Map openers now include Leather Sacks in normal treasure-coffer planning, allowing the existing sack priority to beat Arcane Sphere progression instead of repeatedly targeting the sphere. Treasure-follower loot handling and the existing coffer distance, vertical, and rule gates are unchanged.

## 2026-08-17

- Higher/Lower cash-out recovery now makes one `OpenChest` callback attempt against the hidden `TreasureHighLow` addon after the existing 25-second missing-surface wait, then remains blocked if the callback cannot be sent or verified. It never substitutes a High/Low direction and does not resend the recovery attempt.
- Offline shop routes now preserve a reached, floor-resolved vendor stand-off long enough to use the existing bounded NPC interaction, instead of retargeting an unreachable NPC center. Currency parsing, menu routing, and interaction failure fallback are unchanged.
- Moved Object Explorer Search onto its own full-width row while preserving the existing filters, Clear behavior, and tooltips.

## 2026-08-13

- Exposed the existing shop-reuse switch over IPC as `ADS.SetShopKeepOpen(bool) -> bool`, so an IPC caller buying several items from one vendor can hold the shop open instead of paying a close-and-re-interact cycle per item. Nothing changes unless a caller opts in; `KeepShopOpen` itself, purchase behaviour, and every other endpoint are untouched. Repeated restock testing showed that closing and re-interacting can leave the character in a stale NPC event, causing later interactions to be ignored.
- Fixed the documented way to end such a chain. `KeepShopOpen` told callers to close the held shop with `CancelUtility`, which cannot work: both `Plugin.CancelUtility` and `UtilityAutomationService.Cancel` return immediately unless a purchase is still running, and a held shop exists only once the purchase is terminal. Turning `SetShopKeepOpen` off now closes it (new `ReleaseHeldShopUi`, which never touches a live run's UI), and the remarks no longer point at cancel.

## 2026-08-09

- Fixed treasure-follower BMRAI/VBM targets inside ADS by resolving the live opener through party Content ID, then slot, then exact name or name-plus-world identity, and removing only an authoritative concatenated world suffix that leaves a two-part character name. Both providers, accepted-command state, reapply matching, keys, and diagnostics now share that resolved name; ordinary names, world-like surnames, missing party/world data, regular-duty Slot1 resets, and treasure-exit cleanup remain unchanged.
- Added a default-off XA Database glamour-loot option to Loot Controls. It queries current-character ownership only for equippable loot, requests Need for missing gear, retains live Greed/Pass caps, and preserves the configured base mode with rate-limited diagnostics when IPC data is unavailable or incomplete.
- Added `/ads l` as an exact toggle alias for `/ads loot`.
- Expanded Object Explorer with guarded Foray level/element columns and tooltips, trimmed pipe-separated OR search, and a session-only compact view that keeps actions and filters visible while hiding contextual/status text.
- Debug x64 solution compilation succeeds with zero errors; the two warnings are obsolete-distance references in unchanged `ExplorerSnapshotExportService.cs`. No tests or live-client checks were run.

## 2026-08-02

- Parked ADS in the current Foray, Diadem, and Ocean Fishing territory list (512, 514, 515, 624, 625, 656, 732, 763, 795, 827, 900, 901, 920, 929, 939, 975, 1163, 1252, and 1346). On entry ADS now uses its normal stop/cancel cleanup, stays Idle with an inactive status, skips alliance lookup and all further framework work, and rejects duty, inn, repair, desynth, materia, and shop starts before their side effects. UI, Stop Ownership, and Cancel Utility remain available; normal duties and Eureka Orthos are unaffected.

- Made framework hitch profiling opt-in through Advanced Settings. It now defaults off and adds no per-section `Stopwatch` or delegate allocation during normal framework updates; enable it temporarily to retain the existing 100 ms hitch report, slowest-section diagnostics, and five-second log cooldown.

- Fixed shop purchases terminating as `ui-mismatch` whenever the game pluralises the item name in its confirmation prompt. `ShopConfirmationToken.TryConsumePrompt` matched the sheet item name as an exact whole word, so `Purchase 2 ragworms for 16 gil?` never matched token item `Ragworm` and ADS declined to dispatch Yes to its own confirmation; items whose plural equals their singular, such as `Krill`, masked the bug. Item-name matching now also accepts the `-s`, `-es`, and `-y`/`-ies` plural forms. Quantity and every currency amount are still matched exactly and the ten-second boundary, one-shot consumption, and fail-closed rejection are unchanged, so a prompt that does not describe the exact transaction is still refused.

- Added opt-in reuse of an already-open shop across consecutive purchases (`ShopPurchaseRunner.KeepShopOpen`, surfaced as `UtilityAutomationService.ShopKeepOpen` and `Plugin.SetShopKeepOpen`). It defaults to off, so existing single-purchase behaviour is unchanged. While enabled a successful purchase leaves its shop open, and the next `Start` whose resolved offer matches the visible shop kind enters `validating-ui` directly instead of navigating, interacting and reopening; `ValidateShopUi` still checks the live shop id and row against sheet data, so a different shop is rejected rather than bought from, and a non-matching visible shop fails the start closed. Failed and cancelled runs still close the UI, and the caller owns closing the shop once its batch ends.
- Measured on a gil vendor the character is already standing at, the first purchase is unchanged at ~1.4s while each subsequent purchase from the same shop costs ~0.27-0.33s instead of ~1.4s, because the shop addon open (0.7-1.4s of that) is paid once rather than per item; buying two each of three baits completed in 1.9-2.8s over four consecutive runs.
- Release suite passes 408/408 including the new confirmation-token plural test, and the Release plugin build succeeds with zero errors. Live verification covered gil-shop purchases only.

## 2026-08-01

- Refined Object Explorer into a two-row filter toolbar. `Filter by Lv.` now explicitly enables the session-only character-only Exact/`<=`/`>=` level filter; its first enable uses the local character level, values are positive integers with no maximum, and **Clear filters** disables and resets it while preserving the existing additive filters and row actions.
- Added bounded ADS-owned shop-confirmation diagnostics: each successful purchase callback logs its armed shop/token context and total costs; an unreadable visible `SelectYesno` logs one warning per token; readable mismatches log the expected token and displayed prompt; and Yes dispatch logs success or failure. Exact validation, the ten-second timeout, callback behavior, and fail-closed rejection remain unchanged.
- Focused Debug x64 confirmation-token tests pass 9/9, including exact 99-quantity prompt acceptance and mismatch rejection; the full Debug x64 suite passes 407/407 and the Debug x64 plugin build succeeds with zero warnings and errors. No live purchase or confirmation was run.

## 2026-07-31

- Added an ADS-owned, current-run XA Slave dialog/cutscene skipper fallback. TextAdvance remains authoritative when enabled; `/ads skipper [on|off]` never changes TextAdvance or FrenRider, does not persist configuration, and cleans XA Slave state only after ownership/leaving ends.
- Expanded optional alliance scoping from A/B/C to A-G. Blank scope remains wildcard, while invalid or currently unresolved explicit scope continues to fail closed; schema version remains 1.
- Stopped duty-object rule loads from automatically rewriting saved rule fields through built-in migrations; the rule editor now reloads the manifest exactly as authored.

## 2026-07-30

- Added optional A/B/C alliance scoping to object rules. ADS resolves the current alliance from the typed `_PartyList` UI only while `IPartyList.IsAlliance` is true; blank scope remains wildcard, while invalid or currently unresolved explicit scope fails closed across object rules, manual destinations, cardinal holds, previews, and editor filters. Schema version remains 1.
- ADS outside/inside starts now best-effort-send `/xldisableplugin AutoDuty` before ownership work for shared chat, UI, IPC, and operator-API behavior. Dispatch failure is logged without blocking the requested start; Resume is unchanged. Credit <@196286096726949888>.

## 2026-07-25

- Made `Treasure coffers: OFF` a hard observation-layer ignore for treasure objects and recognized coffer, chest, and treasure-dungeon sack names. The policy now wins before authored object rules and clears remembered loot/suppression state so normal ADS and Fren followers cannot select a live or ghost coffer, while enabled behavior and non-loot progression such as treasure doors remain unchanged.

## 2026-07-23

- Fixed nested shop menus by resolving each unique live handler through `EventHandlerSelector.Option.GlobalIndex`; `LocalIndex` and sheet indexes are now diagnostic only, including the handler `3276827` local-index-3/global-callback-2 regression and its nested callback-4 path.
- Unified owned confirmation and exact-delta verification under one ten-second timeout. Exact readable prompts are accepted once through the boundary, expired or mismatched prompts fail closed, and unreadable prompts time out without resending the purchase callback or changing candidates.
- Replaced fire-and-forget shop navigation cleanup with `vnavmesh.Path.Stop` plus `Path.IsRunning` verification. Interaction, menu selection, retargeting, and identical-cost fallback now wait in a bounded `stopping-navigation` phase; persistent or unverifiable movement terminates as `no-route` before any NPC or purchase callback.

## 2026-07-22

- Expanded deterministic shop purchasing across GilShop, direct and FATE-routed SpecialShop, InclusionShop, Grand Company, and Free Company families; added recursive carrier discovery, live handler-index resolution, deferred gates/balances, audited mixed currencies, exact coproduct verification, family-specific live validation, and owned one-shot confirmations.
- Fixed one-shot shop travel by accepting the owned teleport gil fee after confirmed arrival and waiting for callback-owned confirmation prompts to become readable without weakening exact mismatch or delta checks.
- Added ADS-owned duty camera recovery for idle-camera and all first-person control modes with one shared ten-second cooldown and one-tick ADS-owned key release.
- Added a once-per-entry solo-duty toast explaining `/ads leave` recovery.
- Added five independent, replayable Guided Setup flows plus version-21 one-time-new-install hub behavior and migration-safe optional completion flags.
- Added a default-enabled Settings > Automation toggle for BMRAI/VBM regular-duty follow resets. Disabling it skips the `/bmrai follow Slot1` and `/vbmai follow Slot1` commands on the next regular-duty entry while preserving pending treasure-follow shutdown, treasure opener follow, ordinary cleanup, and treasure-exit cleanup behavior.

## 2026-07-16

- Added a deterministic offline vendor-placement generator and checked-in catalog/audit sourced from local xivdatamine sheets, an offline Garland NPC browse snapshot, and the established ItemVendorLocation location corrections.
- Shop placement resolution now prefers live LGB data, then `Level`, then the embedded offline fallback; explicitly marked corrections can replace known-invalid primary placements while ordinary catalog rows remain fallback-only.
- Added vnavmesh floor resolution for catalog X/Z coordinates after territory entry, live-NPC destination retargeting, and callback-free identical-cost fallback when floor lookup or candidate validation fails.
- Corrected the Versatile Lure regression: the teleport-accessible Limsa merchant is supplied by the offline correction catalog, not `planevent.lgb`.

## 2026-06-14

- Fixed BattleNpc rule shadowing by applying distance/Y gates before effective-rule selection. Failed actionable BattleNpc rules no longer block manual/frontier movement, lower eligible matching rules can win, planner/frontier share one eligibility policy, and Analysis JSON exposes raw/eligible/gate-suppressed monster diagnostics.
- Added authoritative `ADS.IsDutyOwned()` IPC for cross-plugin movement ownership. It reports owned/leaving modes only while live instanced-duty truth is active.
- Rebuilt the object-rules guide around the Object Explorer `RULE` workflow, goal-based class selection, rule-resolution order, common examples, and advanced JSON reference.
- Added class-specific row help, relevant-field tooltips, required/recommended/optional/ignored metadata, red/amber/dim table cues, and missing-required-field validation without clearing ignored stored values.

## 2026-06-09

- Rebuilt Main around persistent operator controls and Overview, Duties, Tools, and Diagnostics tabs while preserving existing actions, disabled-state rules, catalog behavior, live truth, specialist launchers, updates, and JSON copies.
- Replaced the dense Duties table with a searchable responsive catalog/detail dashboard, grouped family filters, and compact collapsible rule coverage; renamed the user-facing `FourPlayerSyncCleared` label to `[Synced Party Cleared]` without changing its enum or JSON value.
- Reorganized Settings into General, Automation, Data & Rules, Advanced, and About tabs without changing configuration fields or data actions.
- Rebuilt compact Controls with full action labels, shared inside/leave disabled rules, grouped shortcuts, concise live status, and unchanged debug-strafe release behavior.
- Replaced the legacy tester-heavy README/GUIDE split with a product overview, operator manual, exhaustive command reference, rule-authoring guide, and troubleshooting/evidence guide.

## 2026-05-18

- Matched treasure-follower door movement to map-opener door follow-through. Followers still never click TreasureDoor/coffer objectives, but they now use the shared through-point, stale-floor detection, and door-frame jiggle recovery before cycling failed same-room door candidates.
- Added a separate, default-off Higher/Lower VFX datamining gate. Normal diagnostics and solver traces stay unchanged; bulky `HigherLowerDiagnostics\Datamine` sessions write only when the experimental datamine checkbox is enabled.

## 2026-05-17

- Fixed treasure-follower routing after the initial manual XYZ. Followers now prefer live TreasureDoor route targets mapped onto static room metadata, skip stale entry/start points once entry progress is proven, and expose the current route key/source and live-door count in status JSON.

## 2026-05-10

- Replaced packaged JSON rule sync with a botologyupdates-backed config cache. ADS now refreshes `duty-object-rules.json`, `dialog-yesno-rules.json`, and `duty-maturity.json` from raw GitHub when cache files are missing, when ownership starts with a cache older than 24h, or when the operator clicks `Update`.
- Added `duty-maturity.json` as the data source for duty clearance/support metadata. The Lumina duty catalog still owns identity/category/level, while maturity rows overlay `ClearanceStatus`, `SupportLevel`, planned-test flag, and support notes.
- Added dialog-rule presets matching object-rule presets: live `DEFAULT`, parked `dialog-rule-presets/*.json`, full-manifest clipboard/disk import/export, create/delete, and `@` reset from the current DEFAULT cache.
- Removed packaged `duty-object-rules.json` / `dialog-yesno-rules.json` from plugin output; built-in JSON is now only a minimal first-run fallback.
- Loosened normal manual `XYZ` arrival to a `2.5y` 3D radius while keeping force-march destinations on the tighter `1y` rule. Manual destinations now ghost as `ManualDestinationNoProgress` if player movement stays under `0.5y` for `12s`, and status/analysis JSON expose the active manual target, distance, progress age, and last manual ghost reason.
- Cleaned Copperbell rule migration/source data: `Sealed Blasting Door` stays `Expendable`, stale `BattleNpc` `Lift Lever` rows are disabled, malformed manual `XYZ` rows with `ObjectWorldCoordinates` are disabled, and lever use stays on the generic non-kind row plus positional ignores.

## 2026-05-09

- Added `Process dialog rules outside owned duties`, enabled by default. Dialog rules now run while ADS is enabled, the character is logged in, and the game is not zoning, including idle, observing, unsupported-duty, and outside-duty states; disabling the setting restores the older owned-or-leaving instanced-duty gate.
- Added `processDialogRulesOutsideOwnedDuty` to Status JSON for tester confirmation.
- Tightened in-combat `BossFight` routing. ADS can still approach a live boss while combat is active, but only until it reaches `5y`; then it targets the boss, stops navigation, and combat-holds that same live boss until combat clears instead of chasing it again.

## 2026-04-12

- Restored the missing GitHub Actions release surface for ADS by adding the standard `build-release.yml` workflow with ADS-specific solution, manifest, and packaged `latest.zip` paths.
- Expanded `GUIDE.md` with a beginner-first duty-maturity testing walkthrough, command list, window tour, rule-authoring quick start, and evidence checklist for helpers who are scouting or validating ADS rules.
- Fixed Brayflox-style manual staging reactivation after talk-NPC progression. ADS now stages a nearby authored `MapXzDestination` / `XYZ` against the live progression interactable, then ghosts that exact manual destination when the interact is actually consumed, instead of only ghosting it later if ADS walks back over the point or hits `BetweenAreas`.
- Reset ADS frontier/manual state and observation memory when leaving supported duty context, so repeated entries into the same duty do not inherit stale ghosts, visited manual waypoints, or remembered manual follow-through from a prior run.
- Added a planner safety seam that refuses stale-ghost backtracking while unvisited human-authored `MapXzDestination` / `XYZ` stages still remain unresolved.

## 2026-04-11

- Added `EventNpc` observation support to the interactable pipeline, so targetable talk NPCs can now be surfaced as live interactables instead of being silently skipped. Brayflox `Goblin Pathfinder` rules can now stay authored as `EventNpc + CombatFriendly`, and stale Brayflox migration no longer rewrites them back to `BattleNpc`.
- Added parked full-manifest rules-editor `PRESET`s alongside the live `DEFAULT` file, including full-manifest clipboard export/import, disk import/export for large manifests, non-deletable `DEFAULT`, and bundled-rule reset into the `DEFAULT` draft via `@`.
- Added `CREATE RULE` to Object Explorer so a new rules-editor row can be seeded directly from the current duty scope, live layer, object kind, base id, and exact object name.
- Added unsaved new-row highlighting/auto-scroll in the rules editor, plus a territory-aware `Layer` dropdown with a blank top option when live sub-area labels are known.
- Added main-window rule-atlas stats and explicit per-duty rule counts in the duty catalog so rule coverage volume is visible without opening the editor.
- Marked `Copperbell Mines` as `[1P Unsync Cleared]` after successful validation.
- Fixed layer-scoped BattleNpc truth leakage. If a visible BattleNpc only has authored layer-scoped rules and none of those layers match the current live sub-area, ADS now suppresses that mob from live monster truth instead of falling back to generic monster targeting. This covers Copperbell-style `Blasting Cap` / `Errant Soul` leakage from `B2` into `First Drop`.
- Fixed Copperbell-style planner dead states where a live BattleNpc was still visible in observations but a stale wildcard `Ignored` row with failing distance/Y gates caused planner to act like no monster existed. `Ignored` / `Follow` BattleNpc rows no longer suppress a live monster when their own gates fail, and the stale `Copper` row was narrowed to `EventObj`.
- Activated rule wait timing. `WaitAtDestinationSeconds` now holds after arrival and before the first direct interact send, and new `WaitAfterInteractSeconds` extends post-interact follow-through before ADS retries or moves on.
- Fixed monster-versus-progression arbitration so if both the live monster and the live progression interactable have active rules, ADS now spends that decision on rule priority first. Distance/Y only break ties or no-rule cases, which fixes Copperbell-style `Firesand` beating a better-priority `Blasting Cap`.
- Collapsed the rules-editor coordinate authoring surface back down to one `Coords` field plus one radius field. `a,b` now means map `X,Z`, `a,b,c` means world `X,Y,Z`, manual destination rows use that same single field, and the runtime storage remains backward-compatible underneath.
- Added positional matching for ordinary same-name rules. `ObjectMapCoordinates` / `ObjectWorldCoordinates` plus `ObjectMatchRadius` now let one row bind to one physical object instance without overloading manual `MapXzDestination` / `XYZ` fields.
- Pending progression interact follow-through now marks the interacted position used as soon as `BetweenAreas` starts. This restores the original Meridianum-style ghosting seam for one-shot objects that immediately transition or leave visibility after interact, such as `Disposal Chute`.
- Required progression-interactable suppression no longer clears on generic large relocations, and ADS now treats a non-disappearing required interactable as consumed when the interact displaces the player materially away from that same object. This restores durable ghosting for one-shot objects like Castrum Meridianum `Disposal Chute`.
- Manual `MapXzDestination` / `XYZ` staging can now beat tied live `Expendable` or `Optional` progression interactables, so Praetorium-style staging rows are not blocked by a same-priority generic `Shortcut`.
- Fixed stale Praetorium `Classification: XYZ` rows that were authored with 3D values in `MapCoordinates` instead of `WorldCoordinates`, and added a built-in migration so older live configs auto-promote those legacy 3-value payloads on load.
- Added a built-in migration for stale Praetorium `Castrum Defense` `Magitek Terminal` ignore rows that were authored as `EventNpc`. ADS now widens those legacy rows to wildcard-kind on load so the ignore still matches the live `EventObj`.
- Added precise manual `XYZ` destinations. New `Classification: XYZ` rows read `worldCoordinates` as authored world X/Y/Z, navigate directly to that point, expose separate XYZ counters in status/analysis JSON, and show up in the rules editor and ghost inspector.
- Fixed Praetorium-style deadlocks where visible progression interactables still blocked manual destinations even though they were outside their own distance/Y rule gates. Frontier/manual blocking now only respects live progression interactables that are actually eligible under the active rule gates, and the planner's idle explanation now says so.
- Added a Praetorium-only mounted combat branch. While `Mounted` stays true in territory `1044`, ADS now bypasses the generic `CombatHold`, reads the live mount-action list from the current mount row, prefers the best nearby cluster for mounted ground-target weapons, and fires the second mounted weapon while enemies stay in range.
- Marked `The Keeper of the Lake` as `[1P Unsync Cleared]`.
- Changed the rules editor's `Current Area + Global` filter to match duty/territory/CFC scope without applying live `Layer`, so same-territory Praetorium rows stay visible while authoring even if the current sub-area is different.
- If `Svc.Condition[Mounted]` becomes true during progression-interactable follow-through, ADS now treats that as a successful consume/use seam, marks the interactable position used, clears the old commitment, and waits for refreshed duty truth instead of retrying the mount object.
- Added `mounted` to the status and analysis JSON so Praetorium-style mount transitions can be validated directly from a capture.
- Fixed Praetorium-style layer swaps by preferring `ClientState.MapId` over the slower `GameMain` map source when both are available, so layer-scoped rules can see the new live sub-area sooner.
- Interactable follow-through is now revalidated against the current live `MapId` and ignore rules, so ADS drops stale committed/pending interactables after a layer swap instead of reusing them in the wrong sub-area.
- Split rule layer scoping into a first-class `Layer` field instead of overloading `DestinationType`, and added automatic migration for older live configs that still stored layer names in `DestinationType`.
- Fixed the rules-editor header tooltips so they wrap to a readable width instead of collapsing into 1-character vertical tooltips.
- Expanded the Ghost Inspector to show the current, remembered, and last-ghosted manual `MapXzDestination` state, making Keeper-style waypoint execution visible in the UI instead of only in the log.
- Widened the global `Automaton Queen` ignore row to an exact-name wildcard-kind ignore so player pet suppression is not coupled to one object-kind guess.

## 2026-04-10

- Added a standalone Ghost Inspector window plus `/ads ghosts`, and ghost recovery now respects the current live `MapId` so stale cross-layer ghosts stop hijacking Keeper recovery.
- Reworked the object rules editor with duty dropdown search + `GLOBAL`, auto-filled `Terr/CFC`, current-area-plus-global filtering, duty sorting, ObjectKind dropdowns, header tooltips, row base64 copy/paste, and tighter default column widths.
- Layer / `DestinationType` now scopes any rule to the current live sub-area, not just `MapXzDestination` rows.
- Manual `MapXzDestination` rows can now intentionally beat a worse live progression interactable when there are no live monsters or follow anchors and the waypoint row has the better priority, making Brayflox-style staging waypoints usable.
- Marked Sastasha as `[1P Unsync Cleared]` after successful validation.
- Changed the Keeper manual Map XZ rows from raw map id `201` to the human-readable active subarea name `Forecastle`, and clarified that `DestinationType` prefers live subarea names over numeric ids when available.
- Added a narrow BattleNpc direct-interact path for `CombatFriendly` rules, so talk targets such as Brayflox's `Goblin Pathfinder` can route/use through the interactable pipeline instead of staying stuck in `liveMonsters`.
- Marked The Stone Vigil as `[1P Unsync Cleared]` after successful validation.
- Added a close-range interact fallback for live interactables: if ADS is already near on X/Z but makes no X/Z progress for `3s`, it stops movement and starts direct-interact retries instead of looping forever in close-nav. This is aimed at vertical/barrier cases such as Sastasha corals.
- Repurposed `DestinationType` on `MapXzDestination` rows into an optional live-map layer selector. Leave it blank for any active submap, or set it to a map row id / map name to restrict that waypoint to one layer.
- Corrected the Stone Vigil boss rule to `Koshchei`, matching the live monster name so the `BossFight` priority path can actually trigger.
- Marked Halatali as `[1P Unsync Cleared]` after successful validation.
- Added BattleNpc-only `BossFight` rule classification plus planner/execution support. Live in-gate boss targets now beat nearby trash, treasure, ghosts, and remembered manual Map XZ follow-through, and they can keep routing through `InCombat` instead of falling into the generic `CombatHold`.
- Fixed the Sastasha coral rules to match the actual live `Blue/Red/Green Coral Formation` names, so those corals stop falling back to `Optional` and can beat the bad wall-path monster case.
- Allowed `CombatFriendly` interactables to bypass the generic `CombatHold` when the planner selects them during `Svc.Condition[InCombat]`, so duties like Keeper can still route to combat-safe progression targets while combat is active.
- Fixed interactable close-nav to use a full XYZ stand-off target instead of flattening to the player's current Y, and now keep interactables in navigation mode until they are actually close in 3D. This addresses vertical/barrier failures like Halatali `Chain Winch`.
- Tightened manual `MapXzDestination` follow-through so remembered manual points still survive transient live-monster visibility but now yield as soon as the planner promotes a live interactable, preventing stale wall-runs after progression targets like `Aetherial Flow` become live.
- Stopped `/vnav` immediately on any `Svc.Condition[BetweenAreas]` frame so sub-area handoffs cannot keep dragging a stale map-flag route after ADS ghosts the target.
- Stopped the frontier service from selecting fresh frontier / manual Map XZ targets during unsafe transition frames, preventing transition-time double ghosting and stale run carryover.
- Moved rule-backed interactable-ghost recovery behind live monster, live progression, live follow-anchor, and frontier / Map XZ choices so stale ghosts no longer steal control from stronger live truth.
- Kept selected manual `MapXzDestination` waypoints sticky during execution, so ADS no longer abandons them just because live monsters or interactables become visible before the configured X/Z arrival point.
- Added a bounded 3-attempt stationary follow-through for `Required` interactables, with immediate cancellation if `Svc.Condition[BetweenAreas]` starts.
- Reworked the duty-catalog readiness summary into four color-coded maturity cards for `[Not Cleared]`, `[1P Unsync Cleared]`, `[1P Duty Support]`, and `[Synced Party Cleared]`.
- Stopped the frontier service from pre-ghosting manual `MapXzDestination` points during the background sweep; they now only ghost on the execution-side 1y X/Z arrival check or on `Svc.Condition[BetweenAreas]`.
- Changed expendable interact follow-through so ADS keeps retrying the same live expendable from the same `<1y` `moveto` stand-off until the object actually disappears.
- Marked Castrum Meridianum as `[1P Unsync Cleared]` and promoted it into the active pilot set after successful validation.
- Added JSON-backed `MapXzDestination` / `MapXZ` manual waypoints. These parse `mapCoordinates` values like `11.3,10.4`, convert them to world X/Z on the current map, use the current player Y, prefer map-flag navigation with `/vnav moveflag`, fall back to direct `/vnav moveto`, and ghost the waypoint at 1y X/Z instead of waiting for an exact navigation finish.
- Froze the top header row in the duty-object and dialog-rule editors, and now ghost the current or last valid manual `MapXzDestination` waypoint as soon as `Svc.Condition[BetweenAreas]` confirms the area handoff.
- Frontier labels, map-flag placement, and manual `MapXzDestination` conversion now honor the live `MapId`, so ADS stops mixing labels from different sub-areas inside the same duty territory.
- Made treasure-coffer follow-through sticky once ADS commits to a coffer, preventing chest-vs-monster objective cycling while ADS is already routing to the selected chest.
- Added global JSON-backed `SelectYesno` dialog rules plus an ADS dialog-rules editor window, seeded with the imperial-identification-key barrier confirmation prompt.

## 2026-04-09

- Bootstrapped the `ADS` repository shell with a Dalamud project, manifest, repo wrapper, workflow, commands, DTR, Ko-fi link, and standard Dhog-style window controls.
- Added the first Lumina-backed 4-man dungeon catalog with pilot-wave support markers for Tam-Tara, Toto-Rak, Brayflox, and Stone Vigil.
- Added a passive duty-context observer, monster/interactable memory, planner explanation surface, ownership shell, and first IPC status/control providers.
- Added staged execution phases, vnav-backed monster/interactable movement, direct interact attempts, recovery ghost handling, object explorer diagnostics, map flags, and human-editable duty-object rules.
- Marked Tam-Tara as `[1P Unsync Cleared]`, added catalog clearance colors/stats, and added Castrum Meridianum plus The Praetorium to the planned test list.
- Added `DutyCompleted` handling so ADS drops owned execution and clears recovery memory when a duty ends.
- Added a territory frontier fallback from Lumina `Level` + `MapMarker` label points so no-object dungeon stretches can advance toward the next map label instead of backtracking through stale ghosts.
- Corrected the frontier label inspector to read labels from each map row's `MapMarkerRange` collection instead of assuming `MapMarker.RowId == Map.RowId`.
- Reused the verified `MapMarkerRange` labels as automation frontier waypoints when the older `Level`/`DataKey` join returns no points, using the player's current Y for label-derived navigation targets.
- Changed label-frontier movement to place an in-game map flag and send `/vnav moveflag`, with direct `/vnav moveto` kept as the fallback if map flag placement fails.
- Deferred active frontier target selection while live monsters or interactables exist, so labels like `Abacination Chamber` are only promoted during actual no-live-object gaps.
- Added live rule-file auto-reload, applied `Ignored` rules to `BattleNpc` observations, treated any matched in-gate human rule as an explicit priority override, and suppressed used progression interactables by duty/object/position until duty reset or large relocation.
- Added `Follow` duty-object rules for live-only NPC anchors such as Cid, deferred progression interactable suppression until after an interaction follow-through window, and made fallback map-label frontier selection heading-aware so Toto-Rak stops choosing behind-route labels like Ser Aucheforne's cell.
- Made BattleNpc objective selection priority-aware, so Required/Follow rules choose targets like The Black Eft before distance tie-breaks when their gates pass.
- Constrained `Follow` to BattleNpc rules only; non-BattleNpc Follow rows are migrated to `Ignored` and ignored at runtime so EventObj rules such as Field Generator cannot hijack follow-anchor planning.
- Marked Toto-Rak and Aurum Vale as `[1P Unsync Cleared]`, and promoted Aurum Vale into the active pilot set after successful validation.
