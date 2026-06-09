# ADS Operator Guide

This guide covers normal operation and the current Main, Settings, and compact Controls layouts. Rule creation and maturity-test detail live in [Rule Authoring](docs/RULE_AUTHORING.md). Failure investigation lives in [Troubleshooting](docs/TROUBLESHOOTING.md).

## Operating Model

ADS separates live observation from owned execution.

- **Idle:** outside an instanced duty and not queued for ADS ownership.
- **Observing:** inside a duty without ADS ownership. ADS reports truth but does not own progression.
- **Owned:** ADS was started outside, started inside, or resumed inside.
- **Leaving:** ADS is handling a requested duty exit.
- **Failed:** ownership attempt or execution failed; inspect status and stop/resume as appropriate.

The planner chooses an objective from current observations, rules, recovery memory, and frontier state. The execution phase reports what ADS is doing with that objective.

## Primary Controls

Main keeps five controls visible above every tab:

| Control | Use | Availability |
|---|---|---|
| **Start Outside** | Queue ADS ownership before entering a duty | Always available |
| **Start Inside** | Claim ownership while already inside | Requires instanced-duty truth |
| **Resume** | Reclaim ownership after stop/reload while inside | Requires instanced-duty truth |
| **Leave** | Request ADS-managed duty exit | Requires ADS ownership |
| **Stop** | Release ADS ownership immediately | Always available |

For an immediate chat stop, use `/ads stop`.

## Main Window

Open with `/ads`.

### Persistent Header

The header shows version, current duty, ownership mode, and execution phase. Primary controls remain visible while switching tabs.

### Overview

Use Overview during normal operation.

- **Current Duty:** duty name, family, readiness, maturity, instanced/catalog truth, unsafe transition state.
- **Planner And Execution:** ownership, phase, planner mode, objective kind, objective, explanation, target distance/vertical delta, execution status, loot status.
- **Active Options:** treasure-coffer setting, loot mode, active object/dialog rule counts, current layer.
- **Warnings:** observing-only and uncatalogued-duty warnings.

Overview displays existing runtime truth only. It does not infer support from a catalog row.

### Duties

Use Duties to inspect validation and rule coverage.

- Search matches duty name, family, expansion, territory ID, and CFC ID.
- Responsive family filters retain their current behavior.
- **All** and **None** toggle family filters.
- Maturity cards summarize clearance status.
- Rule Atlas / Coverage expands to show enabled/global rules, category totals, and coverage signals without dominating the catalog.
- Compact catalog rows show duty, family, maturity, readiness, and explicit rule count while retaining current-duty highlighting, maturity colors, original order, and coverage signals.
- Selecting a catalog row shows level, expansion, party size, content type, territory/CFC IDs, maturity, readiness, planned-test state, explicit rule count, and full support note.
- Wide windows place catalog and selected-duty details side by side. Narrow windows stack them.

Maturity colors:

- Red: `[Not Cleared]`
- Blue: `[1P Unsync Cleared]`
- Yellow: `[1P Duty Support]`
- Green: `[Synced Party Cleared]`

### Tools

Tools groups every Main launcher:

- **Authoring:** Object Explorer, Object Rules, Dialog Rules, Frontier Labels.
- **Treasure And Operations:** Loot Controls, Higher / Lower, Treasure Routes, Reflection.
- **Diagnostics:** Ghost Inspector, Server Events, VFX Explorer.
- **Windows And Settings:** Settings, compact Controls.
- **Data Update:** remote JSON cache update and per-file status.
- **External Links:** Ko-fi, Discord, repository.

The update button is disabled while an update is already running.

### Diagnostics

Use Diagnostics when execution truth is unclear or when preparing a report.

- Territory / Map / CFC identifiers.
- Frontier mode, label/manual-destination counters, target, and heading.
- Treasure role, portal opener, interaction witness, relay, and BMRAI/VBM follow state.
- Live observation and ghost counts.
- **Copy Status JSON** for compact runtime truth.
- **Copy Analysis JSON** for deeper planner/observer/frontier truth.
- Optional JSON preview and sample names when debug sections are enabled.

## Settings

Open from Main > Tools or `/ads config`. Changes save immediately to the same configuration fields used before the layout change.

### General

- Plugin enabled.
- Open Main on load.
- Open compact Controls on load.
- DTR enabled, mode, enabled glyph, disabled glyph.

Clicking the ADS DTR entry opens Main.

### Automation

- Consider treasure coffers in planner.
- Treasure door frame recovery.
- Process dialog rules outside owned duties.

These toggles retain their existing runtime behavior.

### Data & Rules

- Update remote JSON cache and inspect update/cache status.
- Inspect live object-rule, dialog-rule, and maturity paths.
- Open/reload JSON files.
- Open Object Rules, Dialog Rules, and Frontier Labels.

`DEFAULT` is live runtime data. Parked presets do not affect runtime until copied/imported into `DEFAULT`.

### Advanced

**Show debug sections in the Main window** enables Diagnostics JSON preview and observation samples.

### About

Shows version, product summary, pilot summary, support links, and feedback location.

## Compact Controls

Open with `/ads mini` or Main > Tools > Compact Controls.

- Full labels replace abbreviated outside/inside start labels.
- Primary controls use the same inside/leave disabled rules as Main.
- Tool shortcuts open Object Rules, Object Explorer, Dialog Rules, and update cache.
- Live status shows ownership/phase, duty, objective, treasure role/opener, follow status, and execution status.
- Debug strafe controls appear only after `/ads debug on`.
- Closing Controls releases any held debug strafe input.

## Common Workflows

### Start Before Queue

1. Open Main or compact Controls.
2. Select **Start Outside**.
3. Queue and enter the intended duty.
4. Watch Overview for ownership, phase, objective, and warnings.

### Start While Inside

1. Enter an instanced duty.
2. Confirm Main shows correct live duty truth.
3. Select **Start Inside**.
4. Watch objective explanation and execution status.

### Resume After Stop Or Reload

1. Remain inside the duty.
2. Confirm live state has settled after reload/transition.
3. Select **Resume**.

### Stop Or Leave

- Select **Stop** or run `/ads stop` to release ownership immediately.
- Select **Leave** only when ADS owns execution and should request exit.

### Refresh Rules And Data

1. Open Main > Tools or Settings > Data & Rules.
2. Select **Update Remote JSON Cache** or **Update rules cache**.
3. Wait for completion status and refreshed file/byte lines.
4. Inspect load/sync status if runtime data does not reflect the update.

## Specialist Windows

| Window | Primary use |
|---|---|
| Object Explorer | Identify live object name, kind, base ID, distance, targetability, position; seed object rule |
| Ghost Inspector | Inspect monster/interactable ghosts and manual-destination recovery state |
| Frontier Labels | Inspect active-map labels and map resolution |
| Object Rules | Author live/parked object-rule manifests |
| Dialog Rules | Author live/parked yes/no dialog manifests |
| Loot Controls | Configure and inspect loot rolling |
| Higher / Lower | Calibrate and inspect treasure Higher/Lower behavior |
| Treasure Routes | Inspect/edit treasure route data |
| Server Events | Inspect server/map-effect events |
| VFX Explorer | Inspect VFX signals |
| Reflection | Inspect/control BMR reflection integration |

## Related Documentation

- Full chat command reference: [Commands](docs/COMMANDS.md)
- Object/dialog rules and maturity testing: [Rule Authoring](docs/RULE_AUTHORING.md)
- Evidence capture and stuck-state diagnosis: [Troubleshooting](docs/TROUBLESHOOTING.md)
