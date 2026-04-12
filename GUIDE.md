# ADS Guide

## If You Are Helping With Duty Maturity Testing

- ADS has two different rule surfaces:
  - bundled repo rules in `ADS/duty-object-rules.json`
  - live runtime rules in the active Dalamud profile config
- During active scouting/testing, the live runtime file is the one ADS actually uses.
- The rules editor `DEFAULT` preset is the live runtime dataset.
- Parked `PRESET`s are just parked manifests. They do not affect runtime until you copy/import them back into `DEFAULT`.
- For ordinary maturity-testing work, use the live runtime rules first, prove the behavior in-game, and only then promote permanent repo changes.

## What A Helper Is Actually Trying To Do

- Make ADS finish a duty cleanly enough that the duty can be promoted in maturity.
- Find missing or wrong object rules when ADS picks the wrong target, ignores the right target, or gets stuck.
- Capture enough evidence that a permanent repo fix can be made without guessing.

## ADS Windows And Commands

- `/ads`: toggle the main window
- `/ads config`: open Settings
- `/ads obj`: open Object Explorer
- `/ads ghosts`: open Ghost Inspector
- `/ads labels`: open Frontier Labels
- `/ads rules`: open Rules Editor
- `/ads dialogs`: open Dialog Rules
- `/ads ws`: reset all ADS window positions to `1,1`
- `/ads j`: jump ADS windows to visible random positions
- `/ads outside`: queue outside ownership
- `/ads inside`: claim ownership inside the current supported duty
- `/ads resume`: resume ownership inside the current supported duty
- `/ads leave`: request duty leave
- `/ads stop`: drop ADS ownership

## Main Window

- This is the control and truth-summary window.
- Top row:
  - version
  - Ko-fi / Discord / Repo links
  - quick-open buttons for Settings, Objects, Ghosts, Labels, Rules, and Dialogs
- Action row:
  - `Start Outside`: queue ADS before entering the duty
  - `Start Inside`: claim active ownership while already inside a supported duty
  - `Resume`: reclaim ownership after a stop/reload
  - `Leave`: request a duty exit while owned
  - `Stop`: release ADS ownership immediately
- State block:
  - ownership mode
  - execution phase
  - planner mode
  - duty name
  - pilot eligibility
  - treasure toggle
  - active rule count
  - territory / CFC ids
  - objective kind
  - frontier/manual counters
  - current frontier target
  - planner objective/explanation
  - execution summary
- JSON buttons:
  - `Copy Status JSON`: compact runtime state snapshot
  - `Copy Analysis JSON`: deeper planner/observer/frontier state
- Duty catalog:
  - shows all 4-man duties
  - `Rules` column is the explicit authored-rule count for that duty
  - clearance colors are the maturity states
- Observation Summary:
  - live monsters
  - live follow targets
  - live interactables
  - ghost counts

## Settings Window

- Use this for global ADS toggles and quick links.
- Important fields:
  - `Plugin enabled`
  - `Open main window on load`
  - `Enable DTR bar`
  - `Show debug sections in the main window`
  - `Consider treasure coffers in planner`
- Important buttons:
  - `Open rules JSON`
  - `Open frontier labels`
  - `Open rules table`
  - `Reload rules JSON`
- This window also shows the exact live rule-file path ADS is using.

## Object Explorer

- Shows live loaded game objects near the player.
- Best use:
  - confirm exact object name
  - confirm `ObjectKind`
  - confirm distance / Y delta
  - capture `BaseId`
  - confirm whether the object is targetable
- Buttons:
  - `[FLAG]`: place an in-game map flag at that object's world position
  - `CREATE RULE`: seed a new row into the rules editor using the current duty scope, current live layer, object kind, base id, exact name, and world position
- This is usually the fastest way to start a new rule without typing boilerplate.

## Ghost Inspector

- Shows ADS recovery ghosts plus manual-destination state.
- Best use:
  - prove whether ADS is carrying stale monster/interactable ghosts
  - confirm the current manual destination
  - confirm remembered manual follow-through
  - confirm the last ghosted manual destination and reason
- `Current Map Only` is important in multi-layer duties. It filters out stale ghosts from other sub-areas.

## Frontier Labels

- Shows the current live map row's Lumina `MapMarker` labels.
- Best use:
  - see where ADS thinks the named frontier labels are
  - verify active sub-area map resolution
  - drop flags on labels when manual waypoints/frontier routing look suspicious
- This is a label/map debugging tool, not a rule-authoring tool.

## Rules Editor

- This is the main window for duty-object rule work.
- `DEFAULT` is the live runtime rule set.
- Other `PRESET`s are parked manifests only.
- Toolbar:
  - `+ Row`: add a blank row
  - `Save`: write the current preset to disk
  - `Reload From Disk`
  - `Open JSON`
  - `Current Area + Global`: filter to current duty/territory/CFC scope plus global rows
  - `Sort By Duty`
- Preset bar:
  - `Export` / `Import`: full-manifest clipboard transfer
  - `Disk+`: full-manifest file import/export
  - `+`: create parked preset
  - `-`: delete parked preset
  - `@`: reload packaged bundled rules into the live `DEFAULT` draft
- New rows from `+ Row` or `CREATE RULE` stay highlighted until saved.

## Dialog Rules

- Spreadsheet editor for `dialog-yesno-rules.json`.
- These are global `SelectYesno` prompt matches, not duty-scoped object rules.
- Use this only when ADS needs to answer a yes/no prompt while it owns a supported duty.

## The Columns That Matter Most In The Rules Editor

- `Duty`: which duty the row belongs to. `GLOBAL` means wildcard scope.
- `Terr` / `CFC`: numeric scope. Usually auto-filled from the duty dropdown.
- `Kind`: live object kind such as `BattleNpc`, `EventObj`, `EventNpc`, `Treasure`.
- `BaseId`: stable object/base sheet id. Useful when names collide.
- `Name`: object name text.
- `Match`: `Exact` or `Contains`.
- `Class`: what ADS should do with it.
- `Layer`: current live sub-area restriction. Leave blank for any layer.
- `Coords`: one coordinate field for both manual destinations and ordinary positional selectors.
- `R`: positional selector radius for ordinary rows only.
- `Pri`: lower wins.
- `Y`: vertical gate.
- `Dist`: distance gate.
- `Wait-before`: hold before first interact.
- `Wait-after`: hold after successful interact.
- `Notes`: human explanation only.

## What The Classifications Mean In Plain English

- `(none)`: no special override, just scoped matching/gating
- `Ignored`: ADS should pretend this object does not exist
- `Follow`: use this BattleNpc as a live movement anchor, not as a combat or interact target
- `BossFight`: boss BattleNpc that should beat nearby trash/objectives once its gates pass
- `Required`: progression interactable that should usually win when eligible
- `Optional`: interactable that can be used if nothing better is active
- `Expendable`: use/consume object; ADS keeps retrying until it disappears
- `CombatFriendly`: interactable that ADS is allowed to route/use even while combat is active
- `TreasureCoffer`: special optional treasure behavior
- `MapXzDestination`: manual 2D map waypoint
- `XYZ`: manual 3D world waypoint

## Coords And Radius

- `Coords` is the only coordinate field you type into now.
- `a,b` means map `X,Z`.
- `a,b,c` means world `X,Y,Z`.
- On manual destination rows:
  - `a,b` becomes `MapXzDestination`
  - `a,b,c` becomes `XYZ`
- On ordinary rows:
  - `Coords` identifies one physical object instance
  - `R` is the match radius for that object selector

## Typical Maturity-Testing Workflow

1. Open the Main window, Rules Editor, and Object Explorer.
2. Start or resume ADS ownership in the duty.
3. Let ADS fail or choose the wrong thing once.
4. In Main, copy:
   - `Status JSON`
   - `Analysis JSON`
5. In Object Explorer, confirm the exact live object name, kind, base id, position, and whether it is targetable.
6. Use `CREATE RULE` or `+ Row`.
7. Set the smallest rule that solves the problem:
   - correct duty scope
   - correct layer if needed
   - exact name before wildcard matching
   - exact `Kind` if known
   - `BaseId` if names collide
   - positional selector only when same-name objects need to be separated
8. Save the live `DEFAULT` rules and retest immediately.
9. If ADS still behaves strangely:
   - check Ghost Inspector for stale ghosts/manual follow-through
   - check Frontier Labels for wrong active map/sub-area assumptions
10. Once the duty is stable, record what changed and what the tester should verify next.

## What Evidence To Capture When Something Fails

- one fresh `Status JSON`
- one fresh `Analysis JSON`
- the visible `Exec phase`
- the planner objective and explanation line
- if object identity is suspicious:
  - Object Explorer row details
- if ghosting/recovery is suspicious:
  - Ghost Inspector rows
- if sub-area / label resolution is suspicious:
  - Frontier Labels rows or the `Using live map row ... (...)` log line

## Good Rule-Authoring Habits

- Prefer the narrowest rule that explains the behavior.
- Start with exact name matching before switching to `Contains`.
- Add `Layer` only when the duty actually has multiple live sub-areas and the row should not apply everywhere.
- Use positional selectors only when same-name objects really need to be separated.
- Do not use a manual waypoint when a normal object rule is enough.
- Do not promote a repo/bundled rule until the live runtime version is proven.

## Duty-Object Rules

- Rules live in `ADS/duty-object-rules.json` for the bundled copy and in the active Dalamud profile config for runtime edits.
- Lower `Priority` wins.
- For monster-versus-progression arbitration, if both sides have active rules, ADS now spends that comparison on rule priority first. Distance and Y-space only break ties or no-rule cases.
- Equal priorities do not automatically force a required interactable over the best live monster. In that case ADS falls back to distance and Y-space heuristics.
- `PriorityVerticalRadius` and `MaxDistance` act as rule gates for normal monster and interactable rules.
- `ObjectMapCoordinates` / `ObjectWorldCoordinates` are positional selectors for ordinary same-name rows. They are not manual destinations.
- `ObjectMatchRadius` defaults to `6y` when a positional selector is authored and the radius is left blank.
- `WaitAtDestinationSeconds` is the pre-interact arrival hold after ADS reaches interact range and before the first direct interact send for that commitment.
- `WaitAfterInteractSeconds` is the post-interact hold after a successful direct interact send. It extends the existing interact follow-through window before ADS retries or moves on.
- If an `Ignored` or `Follow` BattleNpc row has distance/Y gates and those gates fail, ADS now falls back to treating that BattleNpc as a normal live monster instead of suppressing it into planner limbo.
- The rules editor now collapses those coordinate choices into one `Coords` field:
  - `a,b` means map `X,Z`
  - `a,b,c` means world `X,Y,Z`
  - manual destination rows use that same field too
- `BossFight` and `Follow` are BattleNpc-only classifications.
- `Layer` now scopes any rule to the current live map/sub-area only, not just `MapXzDestination` rows.
- For `BattleNpc`, an authored layer-only rule is now also a truth gate: if a visible mob only matches layer-scoped rows and none of those layers are active, ADS suppresses that mob from live monster truth instead of treating it as a generic unruled monster.
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

## Rules Editor Workflow

- `DEFAULT` is the live runtime rules file. Other `PRESET`s are parked full-manifest datasets stored beside the live config.
- `Export` / `Import` on the preset bar copy or replace the entire manifest through the clipboard.
- `Disk+` opens full-manifest disk import/export for large presets without going through the clipboard.
- `+` creates a new preset from the current draft, `-` deletes the selected parked preset, and `@` loads the packaged bundled rules into the `DEFAULT` draft so you can inspect/reset them before saving live.
- `+ Row` and Object Explorer `CREATE RULE` additions stay visually highlighted until you save, so new rows are easy to find inside a dense ruleset.
- The `Layer` column now uses a territory-aware dropdown when ADS knows the duty's sub-area labels. The blank top option still means “any layer”.
- Object Explorer `CREATE RULE` seeds duty scope, live layer, object kind, base id, and exact object name; fill in classification/priority from there instead of typing the boilerplate every time.

## Positional Same-Name Rules

- Use `ObjectMapCoordinates` when you want one ordinary row to match a specific object instance by player-facing map X/Z, for example one `Lift Lever` but not another same-name lever on the same layer.
- Use `ObjectWorldCoordinates` when Y matters or when you already captured an exact world-space object position.
- If both object coordinate fields are populated, ADS uses `ObjectWorldCoordinates` and ignores `ObjectMapCoordinates`.
- `ObjectMatchRadius` is the selector radius, not the player gate:
  - `ObjectMapCoordinates` uses horizontal X/Z distance to the object's live position.
  - `ObjectWorldCoordinates` uses full 3D distance to the object's live position.
- Positional selectors are evaluated after duty/layer/kind/base-id/name scope but before planner classification and distance/Y gate application.
- `MaxDistance` and `PriorityVerticalRadius` still remain player-relative gates after the row matches. They do not identify which same-name object instance the row belongs to.
- Manual destination fields stay separate:
  - `MapCoordinates` is still only for `Classification: MapXzDestination`.
  - `WorldCoordinates` is still only for `Classification: XYZ`.

## Unified Coords Editor Surface

- In the editor, use `Coords` instead of thinking about four separate coordinate columns.
- ADS interprets the value by shape:
  - `a,b` -> map `X,Z`
  - `a,b,c` -> world `X,Y,Z`
- ADS interprets the same field by row type:
  - manual destination rows: `Coords` becomes the destination point and auto-selects `MapXzDestination` versus `XYZ`
  - ordinary rows: `Coords` becomes the physical object selector
- `R` is only the ordinary-row positional selector radius. Manual destination rows ignore it.
- Runtime storage stays backward-compatible for now, so the live JSON can still carry the older internal fields while the authoring surface stays simple.

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
- If the matched row sets `WaitAfterInteractSeconds`, ADS extends that retry hold before the next required-attempt send.
- If `Svc.Condition[BetweenAreas]` starts during that required-interaction follow-through, ADS ends the retry window immediately.
- If the duty changes to another live `MapId`/layer or the current rules now ignore that target, ADS drops the old interact follow-through and replans from fresh post-transition truth instead of dragging the stale interactable into the new sub-area.
- If `Svc.Condition[Mounted]` becomes true during that follow-through, ADS treats the interact as successful, marks the interactable position used, clears the old commitment, and waits for refreshed live duty truth instead of retrying the same mount object.

## Close-Range Interact Fallback

- If ADS is already within about `2.5y` X/Z and `4y` Y of a live interactable but makes no meaningful X/Z progress for `3s`, execution stops movement and starts direct-interact fallback instead of close-nav looping forever.
- This is a narrow execution fallback for vertical/barrier edge cases such as Sastasha corals. It does not replace the normal full-XYZ stand-off route when ADS is still making progress.

## Combat-Friendly Interactables

- Use `CombatFriendly` for progression interactables that must still be routed and used while `Svc.Condition[InCombat]` is active.
- If the planner emits `Use combat-friendly interactable: ...`, execution now bypasses the generic `CombatHold` and continues the live interactable route during combat.
- `CombatFriendly` on `BattleNpc` or `EventNpc` is the narrow direct-interact seam for talk targets. ADS observes those rows as live interactables instead of monsters or silently dropped NPCs, so it can route to and interact with a targetable NPC such as Brayflox's `Goblin Pathfinder`.
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
