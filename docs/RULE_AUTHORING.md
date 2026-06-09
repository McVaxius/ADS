# ADS Rule Authoring

This guide covers live object/dialog rules, parked presets, and duty-maturity testing. Use [Troubleshooting](TROUBLESHOOTING.md) when collecting failure evidence.

## Data Surfaces

ADS has two distinct rule locations:

- **Live runtime cache:** active Dalamud profile config. ADS reads and executes this data.
- **Maintainer working copy:** `botologyupdates/ads/*.json`. Remote updates refresh live cache from this source.

During scouting, edit and prove live runtime data first. Promote maintainer data only after behavior is verified.

Remote update overwrites live `DEFAULT` cache files. It does not overwrite parked presets.

## Editors And Presets

Object Rules and Dialog Rules use the same preset model:

- `DEFAULT` is live runtime data.
- Other presets are parked full-manifest datasets.
- Saving/importing into `DEFAULT` changes runtime behavior.
- Saving a parked preset does not change runtime behavior.

Common editor controls:

| Control | Purpose |
|---|---|
| `+ Row` | Add a draft row |
| `Save` | Write selected preset |
| `Reload From Disk` | Discard draft and reload selected preset |
| `Open JSON` | Open selected JSON file |
| `Export` / `Import` | Full-manifest clipboard transfer |
| `Disk+` | Full-manifest disk import/export |
| `+` / `-` | Create/delete parked preset |
| `@` | Load current live `DEFAULT` cache into `DEFAULT` draft |

New object rows from `+ Row` or Object Explorer **CREATE RULE** remain highlighted until saved.

## Object Rule Workflow

1. Open Main > Diagnostics, Object Explorer, and Object Rules.
2. Start/resume ADS and let the wrong choice or stuck state occur once.
3. Capture Status JSON and Analysis JSON.
4. In Object Explorer, confirm exact name, object kind, base ID, position, distance/Y delta, targetability, duty, and layer.
5. Use **CREATE RULE** or `+ Row`.
6. Author the narrowest row that explains the desired behavior.
7. Save `DEFAULT`.
8. Retest immediately from clean enough state to prove the row.
9. Check Ghost Inspector and Frontier Labels if behavior still looks stale or layer-dependent.
10. Promote to maintainer data only after repeatable validation.

## Important Object Rule Fields

| Editor field | Meaning |
|---|---|
| `Duty` | Duty scope; `GLOBAL` means wildcard scope |
| `Terr` / `CFC` | Numeric territory/content-finder scope |
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
| `CombatFriendly` | Interactable allowed during combat; supports direct-interact BattleNpc/EventNpc talk targets |
| `TreasureCoffer` | Special optional treasure behavior |
| `TreasureDoor` | Explicit treasure gate/door behavior |
| `MapXzDestination` | Manual 2D map waypoint |
| `MapXzForceMarch` | Manual 2D force-march waypoint |
| `XYZ` | Manual 3D world waypoint |
| `XYZForceMarch` | Manual 3D force-march waypoint |

`BossFight` and `Follow` are BattleNpc-only. Invalid non-BattleNpc rows migrate to `Ignored`.

## Scope And Layer

- Blank duty, `TerritoryTypeId = 0`, and `ContentFinderConditionId = 0` create global scope.
- A non-empty duty name is real duty scope, not notes.
- Duty-name matching tolerates leading `The`, but remains a duty match.
- `Layer` restricts any rule to current live map/sub-area.
- Prefer a human-readable active sub-area name from Main > Diagnostics or Frontier Labels.
- Leave `Layer` blank unless behavior truly differs by sub-area.
- Object Rules **Current Area + Global** filters by duty/territory/CFC and intentionally does not hide rows from another layer in the same duty.

For BattleNpc truth, layer-scoped rows can act as a truth gate: if a visible mob only matches layer-scoped rows and none match current layer, ADS suppresses it instead of treating it as generic unruled monster truth.

## Priority, Gates, And Timing

- Lower `Priority` wins.
- When both a monster and progression interactable have active rules, ADS compares rule priority before distance/Y tie-breaks.
- Equal priorities fall back to distance/Y heuristics; `Required` does not automatically win a tie.
- `PriorityVerticalRadius` and `MaxDistance` gate normal monster/interactable rules.
- `WaitAtDestinationSeconds` delays first interact after arrival.
- `WaitAfterInteractSeconds` extends post-interact follow-through.
- An `Ignored` or `Follow` BattleNpc row whose own gates fail does not suppress that mob into planner limbo.

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

## Duty Maturity Testing

Goal: prove a duty completes cleanly enough to justify maturity promotion and capture enough evidence for durable fixes.

1. Use live runtime `DEFAULT` rules.
2. Start/resume ownership and observe one complete attempt.
3. Fix only proven missing/wrong rules.
4. Capture fresh Status JSON and Analysis JSON for failures.
5. Verify object identity in Object Explorer.
6. Verify recovery state in Ghost Inspector.
7. Verify map/layer assumptions in Frontier Labels.
8. Retest changed behavior and full route.
9. Record party size/sync conditions matching proposed maturity.
10. Promote maintainer JSON and maturity only after repeatable success.

Maturity states:

- `[Not Cleared]`
- `[1P Unsync Cleared]`
- `[1P Duty Support]`
- `[Synced Party Cleared]`

Catalog maturity describes validation. It does not replace live instanced-duty truth.

## Authoring Rules Of Thumb

- Prefer exact name before `Contains`.
- Prefer exact kind and base ID when known.
- Add layer only for real layer differences.
- Use positional selectors only for truly same-name physical instances.
- Use normal object rules before manual waypoints.
- Keep notes human-readable.
- Prove live runtime behavior before maintainer promotion.
