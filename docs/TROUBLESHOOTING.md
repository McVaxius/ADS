# ADS Troubleshooting

## Stop First

When ADS movement or progression is unsafe or unclear:

1. Select **Stop** in Main or compact Controls, or run `/ads stop`.
2. Wait for ownership to leave owned/leaving state.
3. Use Main > Diagnostics to inspect truth before resuming.

`Stop` is always available. Closing compact Controls also releases held debug-strafe input.

## Minimum Evidence Package

Capture evidence while failure state is still visible:

- Main > Diagnostics > **Copy Status JSON**
- Main > Diagnostics > **Copy Analysis JSON**
- Current ownership and execution phase
- Planner objective and explanation
- Execution phase summary
- Duty, territory, map, CFC, and active layer
- Relevant Object Explorer row
- Relevant Ghost Inspector state
- Relevant Frontier Labels state for map/layer issues
- Analysis JSON `rawLiveMonsterCount`, `eligibleMonsterBlockerCount`, and `gateSuppressedMonsterNames`

Enable Settings > Advanced > **Show debug sections in the Main window** for formatted Analysis JSON preview and short live-object samples.

## Stuck-State Inspection

Use this order:

1. **Ownership:** Is ADS observing, owned, leaving, or failed?
2. **Unsafe transition:** Is zoning/cutscene/transition truth still active?
3. **Planner objective:** What does ADS currently want?
4. **Explanation:** Why did that objective win or why is ADS waiting?
5. **Execution phase/status:** What action is running or blocked?
6. **Object truth:** Does Object Explorer show expected name/kind/base ID/targetability?
7. **Rules:** Does expected live `DEFAULT` row match duty, layer, kind, name, position, and gates?
8. **Ghosts:** Is stale recovery truth selected?
9. **Frontier:** Is current map/layer, manual target, or label target wrong?
10. **Treasure follow:** Is opener/follower role or BMRAI/VBM follow state unexpected?

Do not resume until live truth has settled enough to explain the next action.

## Cache Refresh And Reload

ADS live JSON cache includes object rules, dialog rules, duty maturity, and treasure route data.

1. Open Main > Tools or Settings > Data & Rules.
2. Select **Update Remote JSON Cache** / **Update rules cache**.
3. Wait for update completion.
4. Confirm refreshed filenames/byte counts and per-file cache status.
5. Inspect object/dialog/maturity load status.
6. Use explicit reload buttons when testing local live-file edits.

Manual update bypasses the normal stale-age gate and uses cache-busted remote requests. Runtime reload can defer during unsafe transition frames or visible dialog state; inspect status before assuming update failed.

`DEFAULT` is live runtime data. Parked presets are not refreshed and do not execute.

## Specialist Diagnostic Tools

| Tool | Use |
|---|---|
| Object Explorer | Confirm exact live object identity, targetability, distance, Y delta, and position; place flag or seed rule |
| Ghost Inspector | Inspect monster/interactable ghosts, reasons, ages, map IDs, and manual-destination recovery |
| Frontier Labels | Confirm active map row/sub-area and available label targets |
| Object Rules | Inspect scope, layer, classification, priority, gates, and live `DEFAULT` status |
| Dialog Rules | Inspect prompt match/response/delay and live `DEFAULT` status |
| Server Event Explorer | Inspect server/map-effect event signals |
| VFX Explorer | Inspect VFX paths/signals |
| Higher / Lower | Inspect/calibrate treasure card state |
| Treasure Routes | Inspect route metadata and current route data |
| Reflection | Inspect BMR reflection integration |
| Loot Controls | Inspect rolling mode and registrable behavior |

## Common Symptoms

### Start Inside Or Resume Is Disabled

- Confirm current context is an instanced duty.
- Wait for transition truth to settle.
- Catalog metadata is not required; live instanced-duty truth is.

### Leave Is Disabled

- ADS does not currently own execution.
- Use Stop for immediate release; Leave is only an owned-execution exit request.

### ADS Shows Observing Only

- Entering duty did not grant ownership.
- Use Start Inside or Resume after confirming live truth.

### Wrong Object Wins

- Capture Status/Analysis JSON.
- Compare active rules and priorities for both candidates.
- Verify duty, layer, kind, base ID, exact name, positional selector, Y gate, and distance gate.
- Remember failed gate candidates are removed before specificity/priority winner selection; inspect the next eligible matching row.
- Remember lower priority wins; equal priorities fall back to distance/Y heuristics.

### Expected Object Is Missing

- Check Object Explorer targetability/kind/name.
- Check whether an `Ignored`, layer-scoped, or positional row suppresses it.
- Check Ghost Inspector for used/ghosted state.
- Confirm current map/layer.

### ADS Waits With Visible Progression Object

- Read planner explanation.
- Check whether object fails its own distance/Y gates.
- Check for stronger live monster/follow truth.
- Check manual/frontier target state and stale follow-through.

### Visible Monster Blocks Or Does Not Block Frontier

- Inspect Analysis JSON raw, eligible-blocker, and gate-suppressed monster counts/names.
- Failed `Required`, `BossFight`, and `CombatFriendly` BattleNpc rules are non-blocking.
- Failed `Ignored` and `Follow` BattleNpc rules fall back to generic monster-first blocking.
- Unruled monsters remain blockers; large Y mismatches fail the shared vertical sanity check.

### Manual Destination Runs Into A Wall Or Repeats

- Inspect Diagnostics frontier target/counters.
- Inspect Ghost Inspector current/remembered/last-ghosted manual target.
- Confirm `Coords`, layer, and map-space versus world-space choice.
- Capture no-progress age and last ghost reason.

### Multi-Layer Duty Uses Wrong Rules Or Ghosts

- Confirm territory/map/CFC and active layer.
- Use Frontier Labels to verify live map row.
- Filter Ghost Inspector to current map.
- Verify rule `Layer` strings.

### Treasure Follower Does Not Follow Expected Opener

- Inspect Diagnostics treasure role/source, opener/source/locality/age, interaction witness, relay status, post-transit settle, BMRAI/VBM method/result/reason.
- Capture Status and Analysis JSON before state changes.

### Remote Data Looks Stale

- Run manual update.
- Confirm refreshed file names and byte counts.
- Confirm load/sync status after safe reload.
- Open exact live path from Settings > Data & Rules.

### Compact Controls Debug Strafe Looks Stuck

- Run `/ads debug release`.
- Closing compact Controls releases held input.
- `/ads debug off` disables controls and releases input.

## Higher / Lower Evidence

Use `/ads hldebug status` first. Useful commands:

- `/ads hldebug dump`
- `/ads hldebug state`
- `/ads hldebug trace [seconds]`
- `/ads hldebug solver`
- `/ads hldebug folder`

See [Commands](COMMANDS.md) for card/board tagging and texture export syntax.

## Report Checklist

Include:

- What operator expected
- What ADS did
- Exact duty and layer
- Ownership/phase/objective/explanation
- Fresh Status JSON and Analysis JSON
- Object/ghost/frontier evidence relevant to failure
- Rule/cache changes made during test
- Whether issue reproduces after Stop, settled truth, and Resume
