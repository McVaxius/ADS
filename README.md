# AI Duty Solver
---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

[XA and I have created some Plugins and Guides here at -> aethertek.io](https://aethertek.io/)
### Repo URL:
```
https://aethertek.io/x.json
```

---

[Join the Discord](https://discord.gg/VsXqydsvpu)

Scroll down to "The Dumpster Fire" channel to discuss issues / suggestions for specific plugins.

## Plugin Concept

- Show a real Lumina-backed catalog of all 4-man dungeons in the ADS main window.
- Keep the implementation honest: staged execution, passive observation, planner explanation, ownership controls, IPC surfaces, and live movement/interaction only where currently validated.
- Seed active support only for the validated ARR pilot wave: Tam-Tara, Toto-Rak, Brayflox, Stone Vigil, Aurum Vale, and Castrum Meridianum.
- Keep harder duties visible but non-actionable until their duty-profile overlays are written.
- Treat monsters as the default objective, but when both a live monster and a live progression interactable have active rules, lower rule priority wins first. Distance/Y only breaks ties or no-rule cases.
- Status colors in the catalog:
  - Red `[Not Cleared]`
  - Blue `[1P Unsync Cleared]`
  - Yellow `[1P Duty Support]`
  - Green `[4P Sync Cleared]`
- The catalog summary now groups those four readiness states into separate color-coded maturity cards instead of one wrapped status line.

## Current Validation

- Halatali and The Tam-Tara Deepcroft are marked `[1P Unsync Cleared]`.
- The Keeper of the Lake is now also marked `[1P Unsync Cleared]`.
- Sastasha and Copperbell Mines are now also marked `[1P Unsync Cleared]`.
- The Thousand Maws of Toto-Rak, Aurum Vale, and Castrum Meridianum are marked `[1P Unsync Cleared]`.
- The Praetorium is now also marked `[1P Unsync Cleared]`.
- ADS stops owned execution and clears recovery memory when Dalamud reports `DutyCompleted`.
- When the object table goes empty mid-duty, ADS can promote the next unvisited map-label frontier point instead of backtracking through stale ghosts.
- The frontier label inspector reads the `MapMarker` collection referenced by `Map.MapMarkerRange`; this is required for Toto-Rak, where the territory map row and marker collection row are different.
- Automation now also uses those `MapMarkerRange` labels as frontier waypoints when the older level-backed label join fails, so Toto-Rak should prefer labels like `Abacination Chamber` over synthetic heading scouts.
- Frontier labels, fallback map flags, and manual `MapXzDestination` conversion now use the live sub-area `MapId` instead of mixing every map row in a territory, so multi-submap duties such as Keeper should stay inside the current subsection.
- Duty-context map resolution now prefers `ClientState.MapId` when it is available, because that settles faster than the older `GameMain` map source during Praetorium-style sub-area swaps.
- Label frontier navigation now places an in-game map flag and sends `/vnav moveflag`, falling back to direct `/vnav moveto` only if map flag placement fails.
- ADS continues tracking visited frontier labels while live objects are present, but it only exposes a current frontier target once the live monster/interactable list is empty.
- Manual `MapXzDestination` rows can now intentionally beat a worse live progression interactable when there are no live monsters or follow anchors and the waypoint row has the better priority. This makes authored staging points usable for cases like Brayflox talk NPCs instead of being ignored behind a later gate.
- Duty-object rules now auto-reload while ADS is running; `Ignored` can suppress `BattleNpc` rows such as Cid, and used progression interactables are suppressed by stable duty/object/position until duty reset or a large relocation.
- Dialog yes/no rules now auto-reload while ADS is running; these are global `SelectYesno` prompt matches that only fire while ADS owns a supported duty.
- `Required` BattleNpc rules make ADS seek/kill by configured priority before distance tie-breaks.
- `BossFight` BattleNpc rules are a stronger live-boss classification: once the rule distance/Y gates pass, ADS promotes that boss over nearby trash, treasure, ghosts, and remembered manual Map XZ follow-through.
- `Required` progression interactables only hard-override monster-first flow when their effective rule priority actually beats the best live monster; equal priorities fall back to the distance and Y-space heuristics.
- `Follow` rules are BattleNpc-only and turn live NPCs such as Cid into live-only movement anchors that yield to real monsters/interactables and never become ghost targets; non-BattleNpc Follow rows are migrated to `Ignored`.
- `BossFight` rules are also BattleNpc-only; non-BattleNpc BossFight rows are migrated to `Ignored`.
- `CombatFriendly` on `BattleNpc` or `EventNpc` is now the narrow direct-interact seam for talk targets such as Brayflox's `Goblin Pathfinder`; ADS observes those rows as live interactables instead of monsters/ignored NPCs and routes/uses them through the normal interactable pipeline.
- Ghost recovery now carries live `MapId` metadata and only reuses monster/interactable ghosts from the current sub-area, so stale cross-layer hints stop dragging Keeper back and forth between quarterdeck/stern transitions.
- Treasure coffer execution is sticky once ADS commits to the coffer, so a coffer that legitimately wins planning should be approached and handled instead of bouncing back to a monster objective mid-route.
- Expendable interactables now stay in interaction follow-through after ADS sends the interact, and ADS keeps retrying them from the same `<1y` `moveto` stand-off until the object actually disappears.
- Required interactables now get a bounded 3-attempt stationary follow-through window after ADS sends the interact; if `BetweenAreas` starts, the retry window ends immediately.
- Interactable follow-through is now live-map aware and rule-aware. If ADS crosses into another sub-area or the current rules now ignore that target, execution drops the old follow-through and replans instead of carrying the stale interactable across the transition.
- The rules editor's `Current Area + Global` filter now ignores `Layer` and filters by the current duty/territory/CFC scope instead, so Praetorium-style sub-area swaps do not hide other same-territory rules while authoring.
- Live interactables now navigate with a full XYZ stand-off target instead of flattening the close-nav target onto the player's current Y, and ADS stays in navigation mode until the interactable is actually close in 3D. This fixes vertical/barrier cases such as Halatali `Chain Winch`.
- If a live interactable is already within roughly `2.5y` X/Z and `4y` Y but ADS makes no X/Z progress for `3s`, execution now stops movement and starts direct-interact fallback instead of close-nav looping forever.
- Rule wait fields are now live. `WaitAtDestinationSeconds` is the pre-interact arrival hold, and `WaitAfterInteractSeconds` is the post-interact follow-through hold after a successful direct interact send.
- BattleNpc gate suppression no longer turns stale `Ignored` / `Follow` rows into planner dead states when the row's own distance/Y gates fail. This specifically covers Copperbell-style wildcard rows that should not erase visible mobs from planning.
- `CombatFriendly` interactables now bypass the generic `CombatHold` when the planner selects them during `Svc.Condition[InCombat]`, so execution can still route and interact with combat-safe progression targets in duties such as Keeper.
- `BossFight` monster objectives also bypass the generic `CombatHold`, so ADS can keep routing to the live boss target even if nearby trash has already flipped `InCombat`.
- If ADS sends an interact to a progression target and `Svc.Condition[Mounted]` becomes true during follow-through, execution now treats that as a successful consume/use seam, marks the interactable position used, clears the old commitment, and waits for refreshed duty truth instead of retrying the same mount object again.
- While mounted in Praetorium territory `1044`, ADS now bypasses the generic `CombatHold` and uses the current mount row's live mount-action list to fight nearby mobs. It prefers mounted ground-target actions such as `Magitek Cannon` / `Magitek Thunder` at the best nearby cluster, then falls back to the other live mount weapon such as `Photon Stream` / `Magitek Pulse` while enemies remain in range.
- Manual destinations now support both `MapXzDestination` rows with `mapCoordinates` and precise `XYZ` rows with `worldCoordinates`. `XYZ` uses authored world X/Y/Z directly instead of converting map-space X/Z onto the current player Y plane.
- Ordinary same-name rows can now bind to one physical object instance with the new positional selector support, and the rules editor now exposes that through one unified `Coords` field plus radius instead of four separate coordinate columns.
- Frontier/manual blocking is now gate-aware. Live progression interactables that are visible but currently fail their own distance/Y rule gates no longer block manual destinations, which fixes Praetorium-style `WaitingForTruth` deadlocks where a terminal was visible on the layer but not yet eligible.
- Rule-backed interactable ghosts now only enter recovery after live monsters, live progression interactables, live follow anchors, and frontier/Map XZ options are exhausted, so they no longer steal control from stronger live truth.
- `MapXzDestination` rules create manual no-live-object waypoints from player-facing map coordinates such as `11.3,10.4`; ADS converts those to world X/Z with the current player Y, prefers map-flag navigation with `/vnav moveflag`, falls back to direct `/vnav moveto`, keeps a selected manual target sticky through transient live-monster visibility, yields that remembered target once the planner has promoted a live interactable, stops `/vnav` immediately on `BetweenAreas`, does not select fresh manual targets during unsafe transition frames, ghosts the waypoint only once execution reaches within 1y on X/Z or when `BetweenAreas` fires during the area handoff, and now uses `Layer` as the optional live-map selector. Human-readable active subarea names such as `Forecastle` are preferred over raw numeric map ids when you know them. Legacy `DestinationType` layer rows auto-migrate on load.
- `Layer` now filters any rule, not just `MapXzDestination`, against the current live sub-area. If you scope a rule to `Quarterdeck`, `Stern`, `Forecastle`, or a specific map row id, ADS only applies that row while you are actually on that layer.
- If a visible `BattleNpc` only has authored layer-scoped rules and none of those layers match the current live sub-area, ADS now suppresses that mob from live monster truth instead of falling back to generic monster targeting. This covers Copperbell-style `B2` mobs leaking into `First Drop`.
- ADS now has a standalone Ghost Inspector window and `/ads ghosts` command so you can see the current monster/interactable ghost cache, including ghost reason, live map id, age, and coordinates. It also exposes the current, remembered, and last-ghosted manual `MapXzDestination` state so Keeper-style waypoint failures are visible in the UI instead of only in `dalamud.log`.
- The global `Automaton Queen` ignore row is now authored as an exact-name wildcard-kind ignore so player pet suppression is not coupled to one object-kind guess.
- Fallback map-label frontier selection now prefers labels ahead of the current route heading instead of raw sheet order.
- The rules editor now supports parked full-manifest `PRESET`s in addition to the live `DEFAULT` file. You can switch presets, full-manifest export/import through the clipboard, disk import/export for giant manifests, create/delete presets, and load the packaged rules back into the `DEFAULT` draft with `@` before saving them live.
- The rules editor now highlights brand-new rows from `+ Row` or Object Explorer `CREATE RULE` until you save, scrolls them into view, and gives the `Layer` column a live sub-area dropdown with a blank top option whenever the duty territory exposes known map/sub-area labels.
- Object Explorer now has `CREATE RULE`, which seeds a new rules-editor row with the current duty scope, current live layer, object kind, base id, and exact object name.
- The main window duty catalog now shows explicit per-duty rule counts and a top rule-atlas summary with global/grand-total counts, per-class breakdown, and maturity coverage signals.

## Rule Guide

- See [GUIDE.md](GUIDE.md) for the full tester walkthrough, every ADS window/command, duty-maturity rule-testing workflow, duty-object rule authoring notes, positional same-name rule matching, the unified `Coords` editor surface, dialog yes/no rules, global scope behavior, BattleNpc-only `BossFight` / `Follow` behavior, layer semantics, editor-filter semantics, mounted interact follow-through behavior, ghost inspector usage, treasure clamp examples, close-range interact fallback behavior, and `MapXzDestination` / `XYZ` usage.
