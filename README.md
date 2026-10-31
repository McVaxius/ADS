# AI Duty Solver

AI Duty Solver (ADS) is an operator-controlled Dalamud plugin for observing, planning, and executing supported instanced-duty routes. It exposes live duty truth, planner decisions, execution phases, authored object/dialog rules, treasure state, and specialist diagnostics.

ADS is observer-first. Entering a duty alone does not grant ownership; execution requires an operator start/resume or a valid request from an explicitly enabled integration.

Inactivity-kick protection is automatic while ADS is enabled and you are logged in, including while execution is stopped. Disabling ADS or unloading the plugin ends this protection.

## Appearance and language

The first load of this update applies Compact mode once and hides Main's Compact and Transparency controls. Settings > Appearance keeps density, transparency and each main-control visibility choice available; later loads preserve changes made there.

Hindi uses installed shaping fonts. The source now leaves other languages usable when the Hindi menu caption is unavailable, showing a disabled ASCII `Hindi (unavailable)` option. Required text for a selected Hindi UI still requires full validation; on failure, the readable status offers **Use English**, saving English only after an explicit press. Native verification and Linux/Wine acceptance remain pending for this change.

Choose Auto, Picker, LCARS, Glassmorphism, Spatial or Cyberpunk in the shared colour editor. Each new material keeps its own Background, Controls and Text colours; LCARS separately exposes Background and three bar colours. Individual and current-palette resets restore defaults while retaining other palettes. Optional LCARS tones remain muted by default and share their saved setting between Settings and titlebar speakers.

Glassmorphism has flowing waves, independent wave colours and animation enabled by default. Its speed ranges from 0.1 to 10x, defaulting to 1x. Pausing animation keeps wave colours editable. Palette resets retain wave choices; wave resets retain the palette.

Use Settings for shared colour, language, compact spacing and window transparency/fade; titlebar appearance actions change the same saved preferences. Main branding and Main/Quick Controls titles use the packaged ADS icon, keeping its colours and proportions when the title is collapsed. Duty automation remains in Guided Setup and the existing Start, Resume and Stop actions; appearance choices do not change duty ownership or rules.

Titlebars use the packaged icon and a single live status line. **Show titlebar status** and **Detailed titlebar status** in Appearance default on; turn details off to keep concise state and progress, or hide the status entirely. Hover the title for full details. Object Explorer shows its object count and selected name.

Titled windows expose language, compact spacing, transparency and colour actions in their titlebars, including while collapsed. Settings retains the full editors and saved main-control visibility choices. Full and compact layouts retain the same actions and saved choices. Sixteen language choices retain Brazilian Portuguese and Traditional Chinese and include Vietnamese, Indonesian, Polish, Turkish and Hindi. Embedded catalogues cover authored controls, ownership, execution, planner statuses and displayed service diagnostics. Commands, schema identifiers, editable data, external game/plugin names and exception details keep their original values. Managed font preparation includes the selected catalogue, English and non-Hindi native language names; Hindi menu coverage is checked separately. Hindi uses the shared shaped-text renderer across retained controls, editors, tabs and expanded/collapsed window titles; game, GPU, managed-font and IME acceptance remains separate from the offline checks.

Debug x64 compilation, exact embedded-resource/status checks and native selector interaction are verified. The new choices preserve existing language ordinals, control IDs and saved nonlanguage preferences at both densities and scales 1 and 1.5. Per-language offline role-font verification within the diagnostic memory limit, native-client managed-font readiness and full-window visual acceptance remain open.

## Desynthesis

ADS includes policy-driven desynthesis with local presets, skill-up filtering, three source scopes, completed-duty gain tracking, and stable IPC. Open it with `/ads desynth`; Main > Tools > Treasure And Operations has compact Desynth Controls and Extract Materia launchers. See [Desynthesis](docs/DESYNTHESIS.md) and [IPC](docs/IPC.md).

Open `/ads inv`, or use Inventory in ADS Mini, for manual inventory utilities: current-job Recommended Gear, protected armoury-to-bag moves, white-equipment sales, AutoRetainer-list selling, repairs and existing materia/desynthesis/shopping controls. `/ads gear move` stays white-only; `/ads gear move-all` separately includes all unprotected unequipped armoury gear. White-equipment sales keep their preview preference and saved-gearset protections. The equipment-sale IPC lets another plugin explicitly start and observe that same flow; opening the window starts no work.

## Shop Purchasing

ADS can resolve deterministic NPC vendor offers and NPC placements from local game data plus a checked-in offline fallback catalog, then buy an exact additional item quantity with `/ads shop <itemID> <quantity>`. Shop List presets add currency-filtered vendor discovery, targeted refill or spend-until-limit modes, inventory/retainer ownership rules, purchase-free previews, and stable Dad-facing operation IPC without changing the single-purchase contract. Supported families include gil, direct/FATE-routed special exchanges, Inclusion, Grand Company, and Free Company shops. Turn-in, sale, random, ambiguous, and unprovable offers remain callback-free. See [Shop Purchasing](docs/SHOP_PURCHASING.md), [Commands](docs/COMMANDS.md), and [IPC](docs/IPC.md).

## Loot Automation

Open Loot Controls with `/ads loot` or the exact `/ads l` toggle alias. The default-off **Need/Greed missing glamour gear (XA Database)** option checks current-character ownership for equippable loot. Ownership includes all four inventory bags, equipped items, the Armoury Chest, both left and right normal/premium chocobo saddlebags, retainers, armoire and glamour dresser. Live NQ/HQ copies and registration establish ownership; XADB supplies recorded copies for these locations when they are not loaded. An item without a native or recorded copy is treated as missing, including unrecorded or unavailable storage. Recorded copies remain owned regardless of snapshot age; users must open storage and refresh XADB to update its contents. Live Greed/Pass availability still caps the result, and the option remains manual to enable.

## FrenRider Raid mode

Raid mode requires updated builds of both FrenRider and ADS. Open **Raid Mode** from FrenRider Main or Mini. A genuine completed party pull countdown can request one scoped ADS inside start. While armed, Raid replaces queued **Start Outside** takeover and automatic reload/update restarts; deliberate current-duty **Start Inside**, **Resume**, **Stop** and **Leave** remain available. After a confirmed wipe or manual Return, progression waits for a fresh countdown or deliberate inside start/resume. Normal combat choices remain under FrenRider's control.

**ADS Loot** starts off and temporarily controls ADS chest and roll work. Turning it on uses the saved non-Off ADS roll policy, or Need when the saved policy is Off, with the existing native Need/Greed/Pass limits. It never overwrites normal ADS preferences. **Accept Rez** starts checked; Return/respawn stays manual, including after completion.

**Remain until manually exited** starts checked and suppresses both plugins' automatic departure paths. Uncheck it to let Raid alone request exit after final completion: wait up to the configured first-loot appearance limit (default ten seconds), then wait for actual outstanding loot to be distributed. Already-rolled items and hidden loot windows still count. A dead player waits for manual recovery. **Disable Raid Mode** cancels pending Raid work without leaving the duty; closing its window only hides it. See the [Raid IPC contract](docs/IPC.md#frenrider-raid-coordination).

## Installation

Add this custom repository in Dalamud:

```text
https://aethertek.io/x.json
```

Install **AI Duty Solver**, then open it with `/ads`.

## Quick Start

1. Open `/ads`.
2. Review **Overview** for duty, ownership, phase, objective, explanation, warnings, and active options.
3. Use **Start Outside** before queueing, or **Start Inside** after entering an instanced duty.
4. Keep **Overview** or `/ads mini` visible while ADS owns execution.
5. Use **Stop** at any time to release ADS ownership immediately.

Select **Guided Setup** for five independent, replayable setup flows. During an ADS-owned duty, camera recovery can leave idle camera or first-person mode with one shared attempt per ten seconds. Solo duties show the `/ads leave` recovery reminder once per stable entry.

Use **Resume** after a plugin reload or intentional stop while still inside the duty. Use **Leave** only when ADS owns execution and you want ADS to request duty exit.

While ADS owns a duty, native cutscenes hold execution and pause its existing navigation, interaction, recovery and frontier timeout budgets. When the cutscene finishes, those timers resume with their remaining time and the objective is retained. **Stop** cancels owned execution; real zoning keeps its normal transition handling.

## Safety And Stop Guidance

- **Stop** is always available in Main and compact Controls.
- `/ads stop` releases ownership from chat.
- ADS can remain in observing mode without owning movement or progression.
- **Start Inside** and **Resume** require live instanced-duty truth.
- **Leave** is disabled unless ADS owns execution.
- Duty maturity and catalog metadata describe validation status; live instanced-duty truth controls whether inside start/resume is available.
- On an ADS-owned run, ADS uses XA Slave's dialog/cutscene skipper only when `TextAdvance.IsEnabled` is false or unavailable. `/ads skipper [on|off]` controls that fallback for the current run only; it never changes TextAdvance or FrenRider.
- ADS is inactive in the current crowded-activity territory list (512, 514, 515, 624, 625, 656, 732, 763, 795, 827, 900, 901, 920, 929, 939, 975, 1163, 1252, 1346): it parks existing work, refuses automation starts, and keeps the UI, **Stop**, and Cancel Utility available. Ocean Fishing allows the explicitly owned local-only NPC sale used for inventory recovery, without teleporting or entering an inn. After departure, a stale Ocean Fishing context does not block a settled NPC sale. Duty/inn/AutoRetainer ownership, character and UI checks, cancellation and verified completion still apply; other utilities remain excluded.

## Regular-Duty BossMod Follow

The duty list also has a separate per-solo-duty BMR policy: follow the shared choice, preserve BMR, or disable its matching encounter modules during ADS-owned work. Save applies the local choice; data refresh keeps local overrides. The dedicated `duty-bmr-policy.json` travels through BotologyUpdates and the existing validated checkout promotion path. Missing entries preserve BMR. This controls encounter modules independently of autorotation and the regular-duty follow setting. Ownership release restores the modules without undoing independent Queen/Hunt preferences. The prepared feed changes are local until the normal publication process runs.

**Settings > Automation > Enable BMRAI/VBM in regular duties** is enabled by default. When enabled, ADS resets BMRAI and VBM follow targets to `Slot1` on the next regular-duty entry. Disable it to skip only those regular-duty Slot1 commands; treasure opener follow and treasure-exit cleanup are unchanged.

## Feature Map

| Surface | Purpose |
|---|---|
| Main > Overview | Operator truth: duty, ownership, phase, objective, explanation, warnings, options, status |
| Main > Duties | Compact current-duty and coverage summary with launchers for Duty Manager, missing-duty work, and current-duty rules |
| Main > Tools | Authoring, treasure, desynthesis, diagnostics, settings, updates, support links |
| Main > Diagnostics | Territory/map/CFC, map-label and area-boundary frontier, treasure follow, observations, JSON evidence |
| Guided Setup | Five independent Duty, Rules, Utilities, Treasure/Follow, and Diagnostics/Recovery walkthroughs |
| Settings | General, automation, data/rules, advanced display, about/support |
| Compact Controls | Full-label primary actions, tool shortcuts, concise live status |
| Rules Editors | Executable inherited object-rule presets, protected `DEFAULT` shards, searchable multi-context filtering/revert/promotion, responsive compact editing, persisted active dialog presets, partial imports, bulk editing, and duty deep-links |
| Specialist Tools | Object, ghost, frontier, event, VFX, Higher/Lower, treasure, loot, reflection |

Higher/Lower diagnostics default off for new settings. Configuration schema 25 also switches existing diagnostics off once; later saved choices are retained. Enable Diagnostics explicitly and enter a supported treasure dungeon to capture live probes, dumps, traces or addon texture exports. The gameplay solver remains independent of this diagnostic setting. VFX datamining has its own opt-in switch and also operates only in treasure dungeons.

`/ads rules` keeps preset selection, draft actions, filters and selection controls
above the rule grid, whose editor rows meet at their dividers and retain inline controls.
Expand **Advanced**, then choose Preset management, Sharing, Draft editing,
PR-ready checkout or Guided Setup. Each tab shows only its related tools.
Edits still require **Save**; the row **X** uses the same draft deletion and **Undo** flow.

## Object Explorer

`/ads obj` shares its filters between the live object table and an optional game
map. Click a marker or name to highlight and pin its row; selection alone does
not target, move, flag or teleport. Repeated marker clicks cycle overlapping
objects. Drag the map to pan, scroll or use +/− to zoom, and use the target icon
to center on your character. The map hides at narrow widths. Missing map data
stays visibly unavailable; off-layer evidence and height differences remain in
marker details. Object replacement, disappearance, filtering and map changes
clear stale selection. Drag the divider between the map and table to resize the
map; double-click the divider to restore its original width. The width lasts
for this plugin session and leaves enough room for the table. Marker drawing
and clicks follow the resized canvas.

The red flag button arms one map click to place a flag using the current pan and
zoom. Click it again or elsewhere, press Escape or right-click to cancel. Closing, collapsing or hiding
the map, changing territory/map or entering a transition also cancels placement.
The click consumes the action even when placement is refused; it does not infer
world height or start movement.

The **… / Columns** picker controls the informational columns and optional DNav
action for this load. **Diagnostics** contains the full territory/map/layer,
frontier, flag and export details. Map, selection, zoom, disclosure and column
choices are session-only. All original row actions remain as icons with tooltips.

`/ads obj` shows current flag world coordinates. Click resolved XYZ to copy all
three coordinates; **Copy X/Z** is a separate button usable without a resolved
height. Cleared, changed or unavailable flags cannot supply old XYZ.

With compatible DNav loaded, **Columns > Show DNav teleport** exposes a manual row action.
It starts hidden each plugin load and respects DNav's current territory, readiness,
Inside/Outside, blacklist, player filters and shared cooldown. Refusal reasons are
shown on disabled actions and in action status. No DNav mode is enabled; an
unconfirmed reply is never automatically retried.

## Documentation

- [Operator Guide](GUIDE.md)
- [Commands](docs/COMMANDS.md)
- [Shop Purchasing](docs/SHOP_PURCHASING.md)
- [IPC](docs/IPC.md)
- [Rule Authoring](docs/RULE_AUTHORING.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Changelog](CHANGELOG.md)

## Support

Settings > About has **Copy / ZIP Dalamud log**. It snapshots this client's
`dalamud.log` into the plugin's `support-logs` folder and opens that folder.
Share the ZIP manually and remove old exports when no longer needed. A log at
or above 100 MiB may have stopped recording recent activity; ADS warns before
exporting it, so it may be unsuitable for diagnosing the current issue.

For slow frames, enable **Settings > Debug > Enable framework hitch profiler**
briefly. This existing opt-in setting measures ADS framework work and UI drawing
separately, reporting samples from 10 ms with at most one log entry per five
seconds for each category. Framework entries identify the slowest section; both
include automation state and open window types. Compare the affected activity
with ADS idle and its windows closed, then turn profiling off and export the log.
The profiler defaults off and does not start or stop automation.

Settings > Automation > **Disable AutoDuty on ADS start** defaults off. Leave
it off to keep AutoDuty loaded and choose which plugin executes each duty.
Use the manual **Enable AutoDuty** and **Disable AutoDuty** buttons in `/ads mini`
when you want to change its loaded state. These actions do not change that preference.

- [Discord](https://discord.gg/ac6gjDvR8R)
- [Repository](https://github.com/McVaxius/ADS)
- [Ko-fi](https://ko-fi.com/mcvaxius)

When XA Slave is loaded, **Open XA Slave log tools** opens its **Utility > XA Mods** panel, which contains Dalamud Log Cleaner. The existing **Copy / ZIP Dalamud log** action remains separate. Opening the panel does not run cleanup or change XA Slave settings.

### Published object-rule presets

The existing remote updater reads `ads/territories/index.json`. Its optional
`Presets` declarations list each published preset name and its canonical territory
shards; publishing an undeclared folder does not download it. Automatic refresh
uses the existing 24-hour interval, with the existing manual Update JSON action
for an earlier refresh. Updates reload rules and retain the active preset.

Published names use `DEFAULT - `, but the name alone does not grant updater
ownership. ADS writes an installation declaration into each newly installed
preset's `index.json`. An existing unrelated folder with a conflicting name is
preserved and reported in update status; rename the personal preset before a
later update to install the published version.

Updater-installed presets are protected, including with debug enabled. Select
**New Preset** to create a personal copy for edits, sharing or imports. That copy
keeps its own overrides and remains separate from published updates. Missing
contexts inherit the current base DEFAULT shards as in existing custom presets.

Duty Manager's **Activity policies** view lists special activities and exclusions
from installed game data, including names, every CFC/territory variant, actual
content type and automatic/manual/IPC permissions. Battlehall is always excluded.
Other Gold Saucer instances, PvP, seasonal instances and Hall of the Novice require
**Start Inside** or **Resume** in the current instance; an older run cannot carry
ownership into them automatically.
Rules and Object Explorer keep a small **Guide** button visible in regular and
compact toolbars. Each tool offers its guide once in an ordinary titled window,
without dimming or blocking the parent tool. Dismissal, close and focused Escape
are remembered independently of actual guide visits and wizard completion. Rules help
has task choices plus the existing class/field reference; Object Explorer has its
own guide. See the [Operator Guide](GUIDE.md#rules-and-object-explorer-guides) and
[Crystal Tower setup](GUIDE.md#crystal-tower-presets).
