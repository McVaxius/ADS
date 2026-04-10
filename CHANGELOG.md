# Changelog

## 2026-04-10

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
