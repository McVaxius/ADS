# ADS Rule Authoring

This guide covers live object/dialog rules, inherited object-rule presets, Duty Manager, and maturity testing. Use [Troubleshooting](TROUBLESHOOTING.md) when collecting failure evidence.

## Data Surfaces

ADS has two object-rule locations:

- **Local runtime store:** `territories/index.json` plus complete `GLOBAL_rule_objects.json` and `<TerritoryTypeId>_rule_objects.json` shards in the Dalamud profile config.
- **Maintainer checkout:** `botologyupdates/ads/territories/` uses the same indexed DEFAULT contract.

Root shard files are `DEFAULT`. A custom preset is a sparse immediate child folder: a missing file inherits the matching DEFAULT shard, while a present file completely replaces that context. A present empty shard intentionally suppresses the inherited context. Custom-only territories also execute while that preset is active.

The remote updater downloads and validates the complete indexed DEFAULT set before applying it, then recomposes the active preset so inherited contexts update without replacing custom overrides. Invalid or incomplete downloads leave the last valid in-memory rules active.

`ads/duty-object-rules.json` and `ads/duty-object-rules-mature-proposals.json` are frozen compatibility files for older ADS builds. Current ADS does not fetch them and no longer exposes MATURE proposals.

## Editors And Presets

Object Rules use inherited executable presets:

- Selecting or creating a custom preset activates it immediately and persists the selection.
- Missing, deleted, or invalid active presets fall back to `DEFAULT` and persist that fallback.
- The editor presents one combined effective table, not one backing JSON file. Saving materializes every changed context as one complete custom shard; moving a row between scopes writes both contexts.
- The searchable context checklist remembers canonical filenames. No checks means **All**; ordinary clicks toggle multiple contexts without closing the list, and **Current area** selects the live territory directly.
- Every context is labeled as `DEFAULT file`, `Inherited DEFAULT`, `Override file`, `Empty override`, `Custom-only file`, or `No file yet`, with effective row count and unsaved state.
- Batch `Revert context(s) to DEFAULT` remains explicit-selection-only: it deletes every checked saved custom override and restores inheritance; inherited/no-file selections are listed as skipped. A reverted custom-only context disappears because no DEFAULT shard exists.
- Promotion follows the context label literally: with checked contexts it promotes their saved overrides, while **All contexts** (no checks) promotes every saved override in the active custom preset. Inherited DEFAULT and no-file contexts are excluded because they have no custom shard to copy.
- Empty overrides require confirmation; disabling rows is normally safer because it preserves authoring intent.
- Ordinary `DEFAULT` saves are protected and offer the create-preset flow. `/ads debug on` permits direct DEFAULT shard saving for the current session only; disabling debug restores protection.
- Dialog Rules retain their existing separate DEFAULT/parked-preset behavior.

Common editor controls:

| Control | Purpose |
|---|---|
| `+ Row` | Add a draft row; one checked context supplies its exact Global/territory scope, while multiple checks disable the action |
| `Save` | Write selected preset |
| `Reload From Disk` | Discard draft and reload selected preset |
| `Open JSON` | Open the selected preset shard folder |
| `Export` / `Import` | Full-manifest clipboard transfer |
| `Disk+` | Full-manifest disk import/export |
| `+` / `-` | Create and activate/delete a custom preset |
| `@` | Load current live `DEFAULT` cache into `DEFAULT` draft |
| `Contexts` | Search and toggle Global, catalog, current, custom-only, and no-file territories; no checks means All |
| `Revert context(s) to DEFAULT` | Delete every eligible checked custom shard after one confirmation |
| `Promote All saved overrides to PR-ready checkout` / `Promote selected context(s) to PR-ready checkout` | With All, copy every saved custom override; with checks, copy only eligible checked overrides; update the index once for all new contexts |
| `Use checkout` | Validate the entered BotologyUpdates repository root or its `ads/territories` folder and immediately save the canonical Git root; pressing Enter in the path field does the same thing |
| `Open checkout` | Open the last successfully saved canonical repository root |
| `Compact` | Remember a reduced explanatory/secondary-control layout while preserving editing, filters, and context actions |
| `Auto-fit columns` | Refit headers/current values after manual resizing; ordinary edits do not rescan every row, so use this after entering unusually wide content |
| `Rules walkthrough` | Open the replayable Rules & Data wizard directly |
| `Select Visible` / `Clear Selection` | Manage bulk selection without losing hidden selections |
| `Delete Selected` | Confirm and remove selected rows with exact affected duty/global counts |
| `Undo` | Restore the one most recent bulk delete or partial-manifest replacement |
| `Export Duties` / `Export Delta` / `Export Filter` | Export complete selected duty groups, exact selected rows, or the current filtered row set |

Full-manifest clipboard and disk imports open a preview instead of replacing the draft immediately. Incoming rows are associated by CFC, then unique territory, then normalized English name. **Complete duties** replaces complete selected groups, **Delta rows** appends exactly the chosen rows without deduplication, and **Current filter** replaces the exact row indices frozen when the preview opened. Globals require a separate opt-in, and changing a current-filter preview's filters invalidates it.

The editor watches the selected preset's shard snapshots for external changes. A clean draft reloads a valid disk update automatically. A dirty draft stays in memory and shows a conflict instead; saving checks the affected DEFAULT/custom shard snapshots before overwrite. Invalid external JSON empties neither the current draft nor the last valid runtime rules.

Promotion is enabled only for a non-DEFAULT preset with at least one saved custom override in scope, a clean draft, no disk conflict, and a checkout accepted by **Use checkout**. With explicitly checked contexts, only their saved overrides are in scope; with **All contexts** (no checks), every saved override in the active custom preset is in scope, including ordinary, empty, and custom-only shards. Enter either the BotologyUpdates Git worktree root or its `ads/territories` folder; pasted outer quotes and whitespace are normalized, and forks are allowed. Settings and Object Rules share the same in-memory candidate, status, and validity. A successful Use (or Enter) saves the canonical repository root immediately. Invalid input and its exact reason remain visible without replacing the last valid saved root. **Open checkout** and promotion use that saved root, and promotion revalidates it before writing. Promotion copies each scoped saved context in full even when text or row filters hide rows; inherited and no-file contexts are excluded because there is no custom shard to copy. ADS prevalidates all sources, destinations, and the index, writes the index once after all changed shards, reports changed/no-op contexts separately, and requests one confirmation for existing local changes in affected paths plus the existing explicit confirmation for any empty override. It never scans drives, stages, commits, pushes, switches branches, opens a pull request, or changes unrelated repository files.

New object rows from `+ Row` or Object Explorer **CREATE RULE** remain highlighted until saved.

## Object Rule Workflow

1. Open Object Explorer and select **RULE** on the live object.
2. In Object Rules, choose the **Class** matching the goal.
3. Fill red required fields, then amber recommended scope/identity fields.
4. Save the active custom preset. Use session debug mode only when intentionally editing `DEFAULT` directly.
5. Retest immediately from clean enough state to prove the row.
6. Check Ghost Inspector, Frontier Labels, Status JSON, and Analysis JSON if behavior remains wrong.
7. Check the validated contexts and use selected promotion, or deliberately clear the checks and use **Promote All saved overrides to PR-ready checkout**, only after repeatable validation.

Editor field cues:

| Cue | Meaning |
|---|---|
| Red | Required by selected class; bright red means value is missing |
| Amber | Recommended for a narrow, understandable row |
| Normal | Optional behavior, scope, gate, or timing |
| Dim | Ignored by selected class |

Ignored fields remain stored. Selecting a class and showing cues never clears them. Use the row `?` button for focused class help or `[GUIDE]` for the full matrix.

## Rule Resolution Order

1. Scope: duty, territory, CFC, alliance, then layer.
2. Object match: kind, base ID, name/match mode, then optional positional selector.
3. Gates: distance and vertical eligibility.
4. Priority: lower value wins among eligible matching candidates.
5. Behavior/timing: class, wait-before, and wait-after control execution.

ADS removes every identity/scope match that fails `Dist` or `Y` before selecting the effective rule. A more specific or better-priority failed candidate cannot shadow a lower eligible candidate.

## Important Object Rule Fields

| Editor field | Meaning |
|---|---|
| `Duty` | Duty scope; `GLOBAL` means wildcard scope |
| `Terr` / `CFC` | Numeric territory/content-finder scope |
| `Alliance` | Optional alliance-party scope: `(Any)`, `A` through `G` |
| `Kind` | Live object kind such as `BattleNpc`, `EventObj`, `EventNpc`, `Treasure` |
| `BaseId` | Stable base/sheet ID; useful when names collide |
| `Name` | Object name |
| `Match` | Exact or contains name matching |
| `Class` | Planner/observer behavior override |
| `Layer` | Optional live sub-area restriction |
| `Coords` | Manual destination or ordinary positional selector |
| `R` | Ordinary positional-selector radius |
| `Pri` | Priority; lower wins |
| `Y` | Vertical eligibility gate |
| `Dist` | Distance eligibility gate |
| `Wait-before` | Hold after arrival before first interact |
| `Wait-after` | Hold after successful interact |
| `Notes` | Human explanation only |

## Classifications

| Classification | Meaning |
|---|---|
| blank / `(none)` | Scoped match/gating without special override |
| `Ignored` | Suppress matching object from ADS truth |
| `Follow` | BattleNpc-only live movement anchor |
| `BossFight` | BattleNpc-only boss target that can beat nearby trash/objectives once eligible |
| `Required` | Progression interactable that should normally win when eligible |
| `Optional` | Interactable usable when stronger truth is absent |
| `Expendable` | Use/consume object; retry through follow-through until it disappears |
| `CombatFriendly` | Direct-interact BattleNpc/EventNpc allowed during combat; a normal settled interaction suppresses that position until duty reset unless the progression object is explicitly repeatable |
| `TreasureCoffer` | Special optional treasure behavior |
| `TreasureDoor` | Explicit treasure gate/door behavior |
| `MapXzDestination` | Manual 2D map waypoint |
| `MapXzForceMarch` | Manual 2D force-march waypoint |
| `XYZ` | Manual 3D world waypoint |
| `XYZForceMarch` | Manual 3D force-march waypoint |

`BossFight` and `Follow` are BattleNpc-only. Invalid non-BattleNpc rows migrate to `Ignored`.

## Choose Class By Goal

| Goal | Class |
|---|---|
| Hide stale/decorative object | `Ignored` |
| Follow moving BattleNpc | `Follow` |
| Prefer known boss in combat | `BossFight` |
| Force progression interact | `Required` |
| Use only when stronger truth is absent | `Optional` |
| Retry until object disappears | `Expendable` |
| Talk/interact during combat | `CombatFriendly` |
| Mark treasure loot or passage | `TreasureCoffer` / `TreasureDoor` |
| Stage at authored waypoint | `MapXzDestination` / `XYZ` |
| Push through incidental combat | `MapXzForceMarch` / `XYZForceMarch` |
| Hold direct cardinal movement | `CardinalHoldNorth/East/South/West` |

## Scope, Alliance, And Layer

- Blank duty, `TerritoryTypeId = 0`, `ContentFinderConditionId = 0`, and blank `Alliance` create global scope.
- A non-empty duty name is real duty scope, not notes.
- Duty-name matching tolerates leading `The`, but remains a duty match.
- `Alliance` is optional. `(Any)`/blank matches every party; `A` through `G` matches only that live alliance.
- An invalid nonblank alliance or an explicit alliance whose live label cannot currently be resolved fails closed.
- `Layer` restricts any rule to current live map/sub-area.
- Prefer a human-readable active sub-area name from Main > Diagnostics or Frontier Labels.
- Leave `Layer` blank unless behavior truly differs by sub-area.
- Object Rules **Current Area + Global** filters by duty/territory/CFC/alliance and intentionally does not hide rows from another layer in the same duty.

For BattleNpc truth, layer-scoped rows can act as a truth gate: if a visible mob only matches layer-scoped rows and none match current layer, ADS suppresses it instead of treating it as generic unruled monster truth.

## Priority, Gates, And Timing

- Lower `Priority` wins.
- When both a monster and progression interactable have active rules, ADS compares rule priority before distance/Y tie-breaks.
- Equal priorities fall back to distance/Y heuristics; `Required` does not automatically win a tie.
- `PriorityVerticalRadius` and `MaxDistance` gate normal monster/interactable rules.
- `WaitAtDestinationSeconds` delays first interact after arrival.
- `WaitAfterInteractSeconds` extends post-interact follow-through.
- A BattleNpc with only failed actionable rules such as `Required`, `BossFight`, or `CombatFriendly` is not a planner/frontier blocker until one rule becomes eligible.
- An `Ignored` or `Follow` BattleNpc row whose own gates fail preserves generic-monster fallback instead of suppressing that mob into planner limbo.
- Unruled BattleNpc objects remain normal monster-first blockers.

## Planner And Frontier Precedence

- Entered live object rules are first-tier truth once identity, duty/alliance/layer, distance, and Y gates pass.
- `Ignored`, manual destination, and cardinal-hold rows are not live object truth.
- Numeric `Priority` only sorts inside the same tier; generated Lumina labels cannot beat eligible live authored object rules.
- Non-combat `MapXzDestination` / `XYZ` waypoints run only when no eligible live progression object is being skipped.
- `MapXzForceMarch` / `XYZForceMarch` can still bypass incidental combat pressure when no eligible live progression object is skipped.
- Lumina label frontiers, including same-territory game-authored area-boundary markers, and heading-scout frontiers are last fallback, after live objects, authored manual/treasure route points, follow anchors, and recovery ghosts. Area boundaries remain map truth rather than object rules: ADS seeks their native marker until the existing unsafe-transition hold begins, then rebuilds frontier truth from the settled live map.

## Coords And Positional Matching

The editor uses one `Coords` field:

- `a,b` means map `X,Z`.
- `a,b,c` means world `X,Y,Z`.

Interpretation depends on row type:

- Manual destination row: `Coords` is destination.
- Ordinary object row: `Coords` identifies one physical same-name object instance.
- `R` is only the ordinary positional-selector radius.

Runtime JSON remains backward-compatible with separate destination and object-selector fields.

For ordinary positional matching:

- Map coordinates use horizontal X/Z distance.
- World coordinates use full 3D distance.
- World coordinates win when both map and world selectors exist.
- Default selector radius is `6y` when coordinates exist and radius is blank.
- Player-relative `Dist` and `Y` gates still apply after object identity matches.

## Manual Destinations

### Map XZ

- Use `MapXzDestination` or `MapXzForceMarch`.
- Author player-facing map coordinates.
- ADS resolves them against current live map and uses current player Y.
- ADS prefers map-flag navigation and falls back to direct movement.
- Normal selection avoids unsafe transition frames.
- Current target can remain sticky through transient monster visibility, then yield to a promoted live interactable.

### XYZ

- Use `XYZ` or `XYZForceMarch`.
- Author exact world `X,Y,Z`.
- Use when Y matters or map-space conversion is too loose.
- Normal XYZ arrival uses current runtime tolerance; force-march uses tighter arrival behavior.

Non-treasure manual destinations have no-progress recovery. Inspect Main > Diagnostics and Ghost Inspector for active target, remembered target, distance/progress age, and last ghost reason.

Current limitation: player-relative `PriorityVerticalRadius` and `MaxDistance` are not enforced for manual destinations.

## Common Examples

Required object:

```json
{
  "dutyEnglishName": "Copperbell Mines",
  "objectKind": "EventObj",
  "objectName": "Lift Lever",
  "nameMatchMode": "Exact",
  "classification": "Required",
  "priority": 100
}
```

Precise staging point:

```json
{
  "dutyEnglishName": "The Praetorium",
  "classification": "XYZ",
  "worldCoordinates": "154.1,101.9,-34.2",
  "priority": 90
}
```

Cardinal hold:

```json
{
  "classification": "CardinalHoldNorth",
  "worldCoordinates": "123.4,-56.7",
  "maxDistance": 3.0,
  "waitAtDestinationSeconds": 1.5,
  "priority": 100
}
```

## Advanced JSON Reference

The in-plugin `[GUIDE]` window is the authoritative class/field matrix. It lists every stored JSON field as required, recommended, optional, or ignored for each class. Runtime JSON schema and stored fields remain unchanged; the spreadsheet combines destination/object selector coordinates into the visible `Coords` cell while JSON keeps the existing separate fields.

## Dialog Yes/No Rules

Dialog rules are global prompt matches, not duty-scoped object rules.

Important fields:

- `Addon`: defaults to `SelectYesno`.
- `MatchMode`: `Contains` or `Exact`.
- `Response`: `Yes` or `No`.
- `Delay`: seconds before ADS acts; timer resets if watched UI disappears.
- `Notification`: optional minimized notification addon.
- `NotificationCB`: callback text used to restore the notification.

By default, dialog rules can run whenever ADS is enabled, logged in, and not zoning. Disable **Process dialog rules outside owned duties** to require owned/leaving duty execution.

## Duty Manager And Maturity Testing

Main > Duties is a compact current-duty summary. Open **Duty Manager** for the clipped catalog, numeric maturity filters, rule/waypoint/scope-warning coverage, bulk changes, selected-duty notes, and rule deep-links. The Rules deep-link is session-only and preserves the selected editing preset and dirty draft; it uses diagnostic association so redundant scope mismatches remain visible. Runtime rule matching still requires every populated scope field to agree.

Goal: prove a duty completes cleanly enough to justify maturity promotion and capture enough evidence for durable fixes.

1. Use a selected executable custom preset, inheriting untouched contexts from `DEFAULT`.
2. Start/resume ownership and observe one complete attempt.
3. Fix only proven missing/wrong rules.
4. Capture fresh Status JSON and Analysis JSON for failures.
5. Verify object identity in Object Explorer.
6. Verify recovery state in Ghost Inspector.
7. Verify map/layer assumptions in Frontier Labels.
8. Retest changed behavior and full route.
9. Record party size/sync conditions matching proposed maturity.
10. Promote maintainer JSON and maturity only after repeatable success.

Maturity states retain their existing JSON enum values and display numerically:

- `M0` = `NotCleared`
- `M1` = `OnePlayerUnsyncCleared`
- `M2` = `OnePlayerDutySupport`
- `M3` = `FourPlayerSyncCleared`

The legacy `SupportLevel` field remains load-compatible but has no Duty Manager control, label, column, card, or tooltip.

Catalog maturity describes validation. It does not replace live instanced-duty truth.

## Authoring Rules Of Thumb

- Prefer exact name before `Contains`.
- Prefer exact kind and base ID when known.
- Add alliance only when A-G behavior truly differs.
- Add layer only for real layer differences.
- Use positional selectors only for truly same-name physical instances.
- Use normal object rules before manual waypoints.
- Keep notes human-readable.
- Prove active runtime behavior before promoting the complete saved context.
