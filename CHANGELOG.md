# Changelog

## 2026-04-11

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
- Reworked the duty-catalog readiness summary into four color-coded maturity cards for `[Not Cleared]`, `[1P Unsync Cleared]`, `[1P Duty Support]`, and `[4P Sync Cleared]`.
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
