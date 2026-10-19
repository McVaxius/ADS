# ADS Operator Guide

This guide covers normal operation and the current Main, Settings, and compact Controls layouts. Rule creation and maturity-test detail live in [Rule Authoring](docs/RULE_AUTHORING.md). Failure investigation lives in [Troubleshooting](docs/TROUBLESHOOTING.md).

## Desynthesis

Use `/ads desynth` for the run-mode selector, configured policy, preset contents, skill-up, context-menu, clipboard transfer, and ledger controls. Source scopes are inventory only, inventory plus armoury while skipping gearsets, and inventory plus armoury including gearsets.

Run modes: `configured`, `all`, `whitelist`, `last-duty`, `skillups`, `inventory-only`, `everywhere-skip-gearsets`, and `everywhere`.

`last-duty` requires configured source `LastDutyGains` before duty entry. See [Desynthesis](docs/DESYNTHESIS.md).

## Operating Model

ADS separates live observation from owned execution.

- **Idle:** outside an instanced duty and not queued for ADS ownership.
- **Observing:** inside a duty without ADS ownership. ADS reports truth but does not own progression.
- **Owned:** ADS was started outside, started inside, or resumed inside.
- **Leaving:** ADS is handling a requested duty exit.
- **Failed:** ownership attempt or execution failed; inspect status and stop/resume as appropriate.

The planner chooses an objective from current observations, rules, recovery memory, and frontier state. The execution phase reports what ADS is doing with that objective.

## Primary Controls

Main keeps six controls visible above every tab:

| Control | Use | Availability |
|---|---|---|
| **Start Outside** | Queue ADS ownership before entering a duty | Available outside excluded or explicit-request-only activities |
| **Start Inside** | Claim ownership while already inside | Requires instanced-duty truth |
| **Resume** | Reclaim ownership after stop/reload while inside | Requires instanced-duty truth |
| **Leave** | Request ADS-managed duty exit | Requires ADS ownership |
| **Stop** | Release ADS ownership immediately | Always available |
| **Guided Setup** | Open the replayable setup-wizard hub | Always available |

For an immediate chat stop, use `/ads stop`.

### Phoenix Down Recovery With Fren Rider

When Fren Rider requests recovery in a regular four-player dungeon, ADS reports **Phoenix Down recovery; duty progression paused**, stops its movement once, and retains the current owned run. It acknowledges the hold through `ADS.IsPhoenixDownRecoveryHoldActive` before Fren Rider approaches a party corpse or attempts item use. The planner displays **Wait for party revival** while the hold is active.

Fren Rider's **Recovery** tab enables Phoenix Down recovery and nearby outdoor revival by default; combat use defaults off. Limited recovery defaults to five actual started Phoenix Down casts; the alternate time limit is 30 seconds. On exhaustion, Fren Rider releases its recovery hold until a new death is observed. Full stickiness is off by default. A living party healer within 20 yalms of a corpse yields item recovery. The separate give-up switches for no item, unreachable corpse and unavailable ADS acknowledgement default off. Inspect Fren Rider's **Phoenix Down** status for the current blocker or exhausted limit.

After revival, ADS observes the current duty and chooses a fresh objective without automatically stopping or resuming ownership. **Stop** still releases ownership, and explicit **Leave** still takes precedence. If Fren Rider reports that the recovery acknowledgement IPC is unavailable, use an ADS build supporting this contract before expecting coordinated recovery. Live acceptance requires a real Phoenix Down cast, revival, and resumed ADS progression; mcvaxius controls live testing and deployment.

### XA Slave Skipper Fallback

On a successful **Start Outside**, **Start Inside**, or **Resume**, ADS checks `TextAdvance.IsEnabled`. When TextAdvance is enabled, ADS leaves all skipping to it. Otherwise, ADS can enable XA Slave's dialog and cutscene skipper for that ADS-owned run, then turns that fallback off only after ownership (including **Leaving**) has ended. ADS never changes TextAdvance or FrenRider.

Use `/ads skipper` to toggle the fallback for the current run, or `/ads skipper on` and `/ads skipper off` for an explicit choice. `off` suppresses the fallback until the run ends; the next successful ownership start starts fresh. If XA Slave is unavailable, ADS reports the unavailable fallback and makes no skipper change. Successful manual changes show `AIDS Advanced ON` or `AIDS Advanced OFF` once.

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
- **Treasure And Operations:** Loot Controls, Higher / Lower, Treasure Routes, Reflection, Shop Lists, Desynth Controls, Extract Materia.
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

- Integration controls: Disable AutoDuty on ADS start; Enable BMRAI/VBM in regular duties.
- Equipment cleanup: Preview equipment before selling.
- Consider treasure coffers in planner.
- Treasure door frame recovery.
- Process dialog rules outside owned duties.

The BMRAI/VBM setting is enabled by default. When enabled, entering a regular duty resets both follow targets to `Slot1`; when disabled, ADS skips those regular-duty Slot1 commands. A change takes effect on the next regular-duty entry and does not alter treasure opener follow or treasure-exit cleanup.

### Data & Rules

- Update remote JSON cache and inspect update/cache status.
- Inspect live object-rule, dialog-rule, and maturity paths.
- Open/reload JSON files.
- Open Object Rules, Dialog Rules, and Frontier Labels.

Object Rules executes the persisted active preset and shows one combined effective table rather than one backing JSON file. Custom presets are sparse: missing Global/territory shards inherit `DEFAULT`, while present shards replace the whole context, including intentionally empty overrides. Its searchable checklist can filter and batch-revert/promote multiple checked contexts, and the expandable PR-ready checkout section only prepares local PR-ready files. Ordinary `DEFAULT` saves are protected unless debug mode is enabled through `/ads debug on`; this setting is remembered until `/ads debug off`. Dialog Rules retain their separate parked-preset model.

### Debug

**Enable debug mode** uses the same state and input cleanup as `/ads debug on|off`.
Debug mode defaults off. **After death + Return** defaults to **Restart from
entrance**; **Resume** preserves ADS progression tracking. The selector remains
visible but inactive with debug off, retaining your saved selection.

ADS waits for a confirmed death, the exact localized native Return prompt, native
revival with an area transition, and stable living state in the same owned duty.
A callback attempt, arbitrary relocation, Raise or Phoenix revival alone does not
trigger this processing. Restart clears ADS's transient attempts/navigation,
recovery memory, visited/frontier and consumed-rule tracking, then rebuilds from
the returned position. It preserves the selected preset, user rules/settings,
actual dungeon progress and completed/exit intent. Resume invalidates stale
immediate commitments and replans while retaining progression history. Stop,
Leave, debug off, logout, character/duty changes and unload cancel pending work;
this feature never takes ownership or accepts Return itself.

Independent **Show debug sections in the Main window** and **Enable framework
hitch profiler** controls remain available with debug off. The former enables
Diagnostics JSON preview and observation samples; the profiler remains opt-in.

### Appearance

The existing language, colour, density, transparency, titlebar-status and
main-window visibility options retain their saved values and behavior.

### About

Shows version, product summary, pilot summary, support links, feedback location, and **Open Setup Wizards**.

## Crystal Tower presets

Choose **DEFAULT - SYNC - Crystal Tower x 3** for normal synced/full-alliance or
roulette entry, and **DEFAULT - UNSYNC - Crystal Tower x 3** for a deliberately
configured unrestricted private alliance. Each preset contains separate contexts
for the Labyrinth of the Ancients (CFC 92 / territory 174), Syrcus Tower (102 / 372)
and the World of Darkness (111 / 151). The `x 3` name groups the three raids; select
and queue each intended duty through your existing workflow.

The synced preset gives nearby mechanic adds ordinary kill priority ahead of main
bosses. The unsynced preset adds boss-focus rules for burst play while keeping
ordinary mechanic-add rules. Labyrinth has optional, position-matched nearby
shortcuts, with a two-second arrival wait in SYNC and no extra wait in UNSYNC.
Atomos uses a nearby-lane target rule in both. These are source-reviewed target and
shortcut rules; neither preset supplies a complete alliance choreography or a
verified unattended clear. They inherit missing DEFAULT contexts. Unsynced entry
changes level restrictions; alliance entry requirements, Atomos/pad requirements
and other surviving mechanics still need the appropriate participants/actions.

1. Use the existing **Update JSON** action after the new package is published, or
   allow the normal 24-hour refresh. Select the desired named preset in Rules;
   downloading it retains your current selection and starts no automation.
2. Configure the selected combat provider's encounter/rotation setup before the
   run. ADS owns progression and targeting when started. The combat provider and
   participants handle encounter movement, attacks and mechanics; give each
   character one movement owner at a time.
3. If using Fren Rider's ADS handoff, enable its **Alliance** family and an honest
   maturity threshold for the live duty. Fren Rider requires a matching ADS
   catalog identity and clearance before handing over. While ADS owns execution,
   Fren Rider yields its local follow/navigation and combat-state changes. A
   character intended only to follow a leader should remain on its selected Fren
   Rider duty-follow setup instead of also acquiring ADS progression ownership.
4. DAD may coordinate only the profiles and run actions you have explicitly
   configured in its existing workflow. These rule presets do not configure DAD,
   form parties, choose queue modes or add a repetition sequence. Match the actual
   entry mode, party/alliance roles and active ADS preset before each run.
5. Keep each alliance in its assigned lane and arrange Atomos pads, Phlegethon
   platforms, Cerberus roles and other required mechanics with participants or
   the selected encounter provider. Stop ADS when manual movement must take over;
   Resume is an explicit return of progression ownership.

Published presets are protected even with debug enabled. For personal edits or
sharing, select **New Preset** to create a custom copy; the copy has its own
complete-context overrides and remains separate from managed updates. If a
pre-existing personal preset has the same published name, ADS preserves it and
reports the collision. Rename that personal preset before a later update to
install the managed one.

The selected rules use public encounter object IDs from
[BossMod](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Alliance)
and [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossModReborn/tree/main/BossMod/Modules/RealmReborn/Alliance),
and Labyrinth shortcut IDs/positions from
[AutoDuty's Labyrinth path](https://github.com/ffxivcode/AutoDuty/blob/master/AutoDuty/Paths/%28174%29%20The%20Labyrinth%20of%20the%20Ancients.json),
reviewed on 2026-10-18. The public sheet identities agree with the scopes above.
No synced/unsynced gameplay or client acceptance was performed in this change.
## Rules and Object Explorer guides

Rules and `/ads obj` keep a small labeled **Guide** button in their main toolbar,
including compact mode. The first eligible opening offers **Open guide** or
**Dismiss** once per tool. Dismissal, the close button and Escape consume that
one-time offer. Opening the actual guide records a visit; neither showing nor
dismissing an offer completes a wizard. Guide buttons remain available for replay.

Rules help offers tasks for using presets, creating a first rule, class/scope,
understanding a rule that does not win, saving/sharing and the concrete
[Crystal Tower setup](#crystal-tower-presets). Expand **Class and field reference**
for the existing generated class matrix and glossary. Published presets are
read-only; use **New Preset** for personal authoring. Saving changes the complete
context, including rows hidden by filters. Missing custom contexts inherit
DEFAULT, while an intentional empty override suppresses that context's inherited
rows; Revert deletes the saved override.

Object Explorer's guide explains that filters change the viewer. Dist is 3D
player distance and Y is absolute height difference; rule-hit counts precede layer
filtering. Same-map-only remains best-effort. Map/table selection pins an object,
while filtering or disappearance can clear it. **RULE** creates an editor draft:
choose a class and save/activate a personal preset to make it executable. The
position-pin option seeds object XYZ and a radius; the observed BaseId remains in
Notes rather than becoming the match automatically. Map flags and Copy XYZ are
separate from explicit Move/Fly/DNav actions. **Export All JSON** includes all
loaded object-table entries and buffered server events regardless of viewer
filters. Help navigation itself starts no movement, teleport or rule save.
## Guided Setup

The wizard hub contains five separately completed, always-replayable flows: Duty Operations, Rules & Data, Utilities, Treasure & Follow, and Diagnostics & Recovery. Each has overview, prerequisites/safety, and commands/UI steps. Wizard buttons only navigate to existing windows or copy commands; they do not start duties, purchases, updates, or automation. A new installation opens the hub once. Existing version-20 configurations migrate without a forced popup.

## Duty Recovery Notices And Camera

On the first stable tick of each solo-duty entry, ADS shows `If the duty gets stuck, you can leave with /ads leave.` The reminder does not require ownership and re-arms only after a stable duty exit or logout.

Camera recovery is ownership-bound. While ADS owns a stable instanced duty, idle camera has priority and is stopped with `/icam`; first-person mode is corrected through the player's configured camera-mode keyboard binding. Both actions share one ten-second attempt cooldown, and ADS releases only key state it injected on the next framework tick.

## Compact Controls

Open with `/ads mini` or Main > Tools > Compact Controls.

- Full labels replace abbreviated outside/inside start labels.
- Primary controls use the same inside/leave disabled rules as Main.
- Tool shortcuts open Loot Controls, Object Rules, Object Explorer, Dialog Rules, Shop Lists, and update cache.
- Enable/Disable QSTcomp and HealBot send Dalamud's plugin commands for `QSTCompanion` and `Coppelia`. Reset RSR Healing sends RSR Off and the eleven healing defaults restored by Coppelia; it works without HealBot loaded. ADS chat reports sent or failed requests.
- Live status shows ownership/phase, duty, objective, treasure role/opener, follow status, and execution status.
- Debug strafe controls appear while debug mode is enabled through `/ads debug on`. ADS remembers debug mode across reloads, but never restores held input.
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

### Author An Object Rule

1. Open Object Explorer and select **RULE** on the live object.
2. In Object Rules, choose the goal-matching **Class**.
3. Fill red required fields first, then amber recommended identity/scope fields.
4. Save the active custom preset, or intentionally enable debug mode before a direct `DEFAULT` save.
5. Retest immediately.

Field cues are class-specific: red is required, amber recommended, normal optional, and dim ignored. Class selection and cues never clear ignored stored values. Use the row `?` button for focused class help or `[GUIDE]` for the full goal/class matrix and advanced JSON field reference.

## Specialist Windows

| Window | Primary use |
|---|---|
| Object Explorer | Identify live object name, kind, character level, guarded Foray level/element (or `—`), base ID, distance, targetability, and position; separate trimmed search terms with `|` for OR matching; use the session-only **Compact** checkbox to retain export/action buttons and filter labels while hiding contextual/status text; use `Filter by Lv.` for the existing Exact/`<=`/`>=` character-level filter; seed object rule |
| Ghost Inspector | Inspect monster/interactable ghosts and manual-destination recovery state |
| Frontier Labels | Inspect active-map labels, game-authored area-boundary markers, and map resolution |
| Object Rules | Author executable inherited presets in a responsive/compact table; search and check multiple labeled contexts; batch-revert overrides or batch-promote clean saved shards to a configured BotologyUpdates checkout |
| Dialog Rules | Author live/parked yes/no dialog manifests |
| Loot Controls | Open with `/ads loot` or `/ads l`; configure base rolling and the default-off XA Database glamour option, which requests Need for missing equippable gear while preserving live Greed/Pass caps and the selected base mode on incomplete IPC data |
| Shop Lists | Open with `/ads shopper`; manage persisted desired-total vendor lists, import TeamCraft/Crafting as a Service/Artisan clipboard data, inspect live and retainer ownership, and run sequential purchases |
| Higher / Lower | Calibrate and inspect treasure Higher/Lower behavior |
| Treasure Routes | Inspect/edit treasure route data |
| Server Events | Inspect server/map-effect events |
| VFX Explorer | Inspect VFX signals |
| Reflection | Inspect/control BMR reflection integration |

## Related Documentation

- Full chat command reference: [Commands](docs/COMMANDS.md)
- Object/dialog rules and maturity testing: [Rule Authoring](docs/RULE_AUTHORING.md)
- Evidence capture and stuck-state diagnosis: [Troubleshooting](docs/TROUBLESHOOTING.md)
