# ADS Guide

## Duty-Object Rules

- Rules live in `ADS/duty-object-rules.json` for the bundled copy and in the active Dalamud profile config for runtime edits.
- Lower `Priority` wins.
- Equal priorities do not automatically force a required interactable over the best live monster. In that case ADS falls back to distance and Y-space heuristics.
- `PriorityVerticalRadius` and `MaxDistance` act as rule gates for normal monster and interactable rules.
- `BossFight` and `Follow` are BattleNpc-only classifications.
- `Layer` now scopes any rule to the current live map/sub-area only, not just `MapXzDestination` rows.
- Legacy rows that stored layer scope in `DestinationType` auto-migrate on load into the real `Layer` field.

## Boss-Fight BattleNpc Rules

- Use `Classification: BossFight` on live boss BattleNpc rows that should beat nearby trash, treasure, ghosts, and remembered manual Map XZ follow-through once the rule gates pass.
- `BossFight` uses the same `PriorityVerticalRadius` and `MaxDistance` gates as other BattleNpc rules, so “near one” is controlled by the row's own distance/Y limits.
- If the planner emits `Engage boss-fight target: ...`, execution treats that as a monster route and bypasses the generic `CombatHold`.
- Non-BattleNpc `BossFight` rows are migrated to `Ignored` on load.

## Scope Fields

- Leave `DutyEnglishName` blank, `TerritoryTypeId` at `0`, and `ContentFinderConditionId` at `0` for a truly global rule.
- If `DutyEnglishName` is non-empty, ADS treats it as duty scope, not freeform notes text.
- Duty name matching is article-tolerant for leading `The`, but it is still a real duty-name match.
- If `Layer` is non-empty, the rule only matches on that live sub-area. Use `frontier.activeMapName` / `Using live map row ... (...)` to capture the correct layer string.
- The rules editor's `Current Area + Global` filter is intentionally broader than runtime matching: it shows rules for the current duty/territory/CFC scope plus global rows, and does not hide them just because the current live `Layer` is different.

## Treasure Coffers

- A global treasure clamp rule should usually be scoped with blank duty and zero ids.
- Example pattern:
  - `ObjectKind`: `Treasure`
  - `ObjectName`: `Treasure Coffer`
  - `Classification`: `Expendable`
  - `PriorityVerticalRadius`: `5`
  - `MaxDistance`: `20`

## Expendable Interactables

- Live interactables now route to a full XYZ stand-off target that preserves the interactable's real Y instead of flattening to the player's current floor.
- ADS keeps navigating while the interactable is still far in 3D, even if the XZ gap looks small, so barrier / upper-lower floor cases do not degrade into flat-plane wall-running.
- Once ADS sends an interact to an `Expendable`, it keeps that object in follow-through instead of immediately marking it used.
- If the object is still live after the settle window, ADS retries it from the same `<1y` X/Z `moveto` stand-off until the object disappears.

## Required Interactables

- Once ADS sends an interact to a `Required` interactable, it now holds still and retries up to `3` sent attempts before releasing that retry window.
- If `Svc.Condition[BetweenAreas]` starts during that required-interaction follow-through, ADS ends the retry window immediately.
- If the duty changes to another live `MapId`/layer or the current rules now ignore that target, ADS drops the old interact follow-through and replans from fresh post-transition truth instead of dragging the stale interactable into the new sub-area.
- If `Svc.Condition[Mounted]` becomes true during that follow-through, ADS treats the interact as successful, marks the interactable position used, clears the old commitment, and waits for refreshed live duty truth instead of retrying the same mount object.

## Close-Range Interact Fallback

- If ADS is already within about `2.5y` X/Z and `4y` Y of a live interactable but makes no meaningful X/Z progress for `3s`, execution stops movement and starts direct-interact fallback instead of close-nav looping forever.
- This is a narrow execution fallback for vertical/barrier edge cases such as Sastasha corals. It does not replace the normal full-XYZ stand-off route when ADS is still making progress.

## Combat-Friendly Interactables

- Use `CombatFriendly` for progression interactables that must still be routed and used while `Svc.Condition[InCombat]` is active.
- If the planner emits `Use combat-friendly interactable: ...`, execution now bypasses the generic `CombatHold` and continues the live interactable route during combat.
- `CombatFriendly` on `BattleNpc` is the narrow direct-interact seam for talk targets. ADS observes those rows as live interactables instead of monsters, so it can route to and interact with a targetable NPC such as Brayflox's `Goblin Pathfinder`.
- If you author a manual `MapXzDestination` staging row with a better priority than a currently visible later progression interactable, ADS can now use that staging point first as long as no live monsters or follow anchors are visible.

## Praetorium Mounted Combat

- While `TerritoryTypeId == 1044` and `Svc.Condition[Mounted]` stays true, ADS now bypasses the generic `CombatHold`.
- ADS resolves the current mount's live mount-action list from the mount sheet instead of hardcoding one Praetorium action id pair.
- If the mount exposes a ground-target weapon, ADS prefers the best nearby enemy cluster and fires that first.
- ADS then uses the other live mount weapon on the nearest valid nearby enemy while targets remain in range.
- If enemies are still nearby but the mount actions are cooling down, ADS stays in the mounted-combat phase instead of dropping back into generic combat hold.

## Map XZ Destinations

- Use `Classification: MapXzDestination`.
- `Layer` is optional for `MapXzDestination` rows. Leave it blank to allow that waypoint on any active submap, or set it to the live subarea name / map row id to restrict the waypoint to one layer.
- Prefer the human-readable active subarea name from `frontier.activeMapName` or the one-shot `Using live map row ... (...)` log line. Example: `Forecastle`.
- `MapCoordinates` should be player-facing map coordinates such as `11.0,11.1`.
- ADS converts those map coordinates into world X/Z on the live current map row and uses the current player Y for navigation.
- ADS prefers map-flag navigation with `/vnav moveflag` for manual Map XZ points and falls back to direct `/vnav moveto` if map-flag placement fails.
- ADS does not pre-ghost manual Map XZ points during the background frontier scan; the point stays live until execution reaches the 1y X/Z arrival rule or `BetweenAreas` fires.
- Once ADS has selected a manual Map XZ point, it keeps following through on that remembered target through transient live-monster visibility instead of dropping it immediately.
- If the planner later promotes a live interactable, ADS yields the remembered manual Map XZ target and switches to that live interactable.
- ADS does not select fresh frontier or manual Map XZ targets while unsafe transition flags are active; it waits for post-transition truth to settle first.
- If ADS hits `Svc.Condition[BetweenAreas]` while a manual Map XZ point is the current or last valid target, it stops `/vnav` immediately and ghosts that point instead of waiting for a 1y X/Z arrival check.
- The active target's resolved world position is exposed in the status / analysis JSON as `frontier.currentTargetPosition`.

## XYZ Destinations

- Use `Classification: XYZ`.
- Put authored world coordinates in `WorldCoordinates` as `x,y,z`, for example `154.1,101.9,-34.2`.
- `Layer` is optional for `XYZ` rows. Leave it blank to allow that waypoint on any active submap, or set it to the live subarea name / map row id to restrict the waypoint to one layer.
- `XYZ` is for precise same-subarea staging where `MapXzDestination` is too loose because Y matters and you do not want current-player-Y conversion.
- ADS routes directly to the authored world X/Y/Z point with `/vnav moveto` and ghosts the destination once execution reaches the 1y 3D arrival rule.
- Status / analysis JSON now expose `frontier.manualXyzDestinationCount`, `frontier.visitedManualXyzDestinations`, and `frontier.currentTargetWorldCoordinates` for active XYZ points.

## Gate-Aware Manual Blocking

- Frontier/manual destination selection now only treats live progression interactables as blockers if they pass their own active distance/Y rule gates.
- This matters in duties such as Praetorium: a visible `Magitek Terminal` that is still outside its own `PriorityVerticalRadius` or `MaxDistance` no longer blocks a manual staging row and leave ADS stuck on `WaitingForTruth`.
- The planner's final idle explanation now calls that out directly instead of pretending no interactables were visible.

## Recovery Ghosts

- Rule-backed interactable ghosts are recovery-only hints. They should not beat live monsters, live progression interactables, live follow anchors, or available frontier / Map XZ routes.
- Monster/interactable ghost recovery is now current-sub-area aware. ADS only promotes ghosts from the active live `MapId`, so stale Keeper ghosts from another layer should not bounce you between sub-areas anymore.
- If you see a rule-backed interactable ghost selected while those stronger live signals still exist, that is a bug.

## Ghost Inspector

- Use the main-window `Ghosts` button or `/ads ghosts`.
- This window shows the current monster/interactable ghost cache with type, classification, ghost reason, live `MapId`, age, position, and a map-flag button.
- It also shows the current, remembered, and last-ghosted manual `MapXzDestination` state, so a Keeper waypoint can be verified there even though it is not an observation-memory ghost.
- `Current Map Only` is useful when checking whether a stale ghost belongs to another sub-area.

## Frontier Labels

- ADS now resolves the active sub-area by live `MapId` and only loads frontier labels from that map row.
- Duty-context map resolution prefers `ClientState.MapId` when available, because it settles faster than the older `GameMain` map source during some multi-layer transitions such as Praetorium.
- This prevents multi-submap duties from mixing labels across sections of the same territory.

## Current Limitation

- `PriorityVerticalRadius` and `MaxDistance` are not yet enforced for `MapXzDestination` rows.
- The mounted-success seam is intentionally execution-local. It resolves ADS-owned post-interact follow-through, but it does not by itself suppress an already-mounted object if ADS never sent the interact in the current run.

## Dialog Yes/No Rules

- Dialog rules live in `ADS/dialog-yesno-rules.json` for the bundled copy and in the active Dalamud profile config for runtime edits.
- These rules are global `SelectYesno` prompt matches. They are not duty-scoped.
- ADS only applies them while it owns a supported duty, so they do not become a global background yes-clicker.
- `MatchMode` supports `Contains` and `Exact`.
- `Response` supports `Yes` and `No`.
- The bundled starter rule matches `imperial identification key to deactivate the barrier` and clicks `Yes`.
