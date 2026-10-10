# AI Duty Solver

AI Duty Solver (ADS) is an operator-controlled Dalamud plugin for observing, planning, and executing supported instanced-duty routes. It exposes live duty truth, planner decisions, execution phases, authored object/dialog rules, treasure state, and specialist diagnostics.

ADS is observer-first. Entering a duty does not automatically grant ADS ownership unless the operator starts or resumes execution.

## Appearance and language

The first load of this update applies Compact mode once and hides Main's Compact and Transparency controls. Settings > Window appearance keeps density, transparency and each main-control visibility choice available; later loads preserve changes made there.

Hindi uses installed shaping fonts. The source now leaves other languages usable when the Hindi menu caption is unavailable, showing a disabled ASCII `Hindi (unavailable)` option. Required text for a selected Hindi UI still requires full validation; on failure, the readable status offers **Use English**, saving English only after an explicit press. Native verification and Linux/Wine acceptance remain pending for this change.

Use Settings for shared colour, language, compact spacing and window transparency/fade; the optional Main selectors change the same saved preferences. Main branding and Main/Quick Controls titles use the packaged ADS icon, keeping its colours and proportions when the title is collapsed. Duty automation remains in Guided Setup and the existing Start, Resume and Stop actions; appearance choices do not change duty ownership or rules.

The main header and Settings share the language, colour and compact preference. Full and compact layouts retain the same actions and saved choices. Sixteen language choices retain Brazilian Portuguese and Traditional Chinese and include Vietnamese, Indonesian, Polish, Turkish and Hindi. Embedded catalogues cover authored controls, ownership, execution, planner statuses and displayed service diagnostics. Commands, schema identifiers, editable data, external game/plugin names and exception details keep their original values. Managed font preparation includes the selected catalogue, English and non-Hindi native language names; Hindi menu coverage is checked separately. Hindi uses the shared shaped-text renderer across retained controls, editors, tabs and expanded/collapsed window titles; game, GPU, managed-font and IME acceptance remains separate from the offline checks.

Debug x64 compilation, exact embedded-resource/status checks and native selector interaction are verified. The new choices preserve existing language ordinals, control IDs and saved nonlanguage preferences at both densities and scales 1 and 1.5. Per-language offline role-font verification within the diagnostic memory limit, native-client managed-font readiness and full-window visual acceptance remain open.

## Desynthesis

ADS includes policy-driven desynthesis with local presets, skill-up filtering, three source scopes, completed-duty gain tracking, and stable IPC. Open it with `/ads desynth`; Main > Tools > Treasure And Operations has compact Desynth Controls and Extract Materia launchers. See [Desynthesis](docs/DESYNTHESIS.md) and [IPC](docs/IPC.md).

## Shop Purchasing

ADS can resolve deterministic NPC vendor offers and NPC placements from local game data plus a checked-in offline fallback catalog, then buy an exact additional item quantity with `/ads shop <itemID> <quantity>`. Shop List presets add currency-filtered vendor discovery, targeted refill or spend-until-limit modes, inventory/retainer ownership rules, purchase-free previews, and stable Dad-facing operation IPC without changing the single-purchase contract. Supported families include gil, direct/FATE-routed special exchanges, Inclusion, Grand Company, and Free Company shops. Turn-in, sale, random, ambiguous, and unprovable offers remain callback-free. See [Shop Purchasing](docs/SHOP_PURCHASING.md), [Commands](docs/COMMANDS.md), and [IPC](docs/IPC.md).

## Loot Automation

Open Loot Controls with `/ads loot` or the exact `/ads l` toggle alias. The default-off **Need/Greed missing glamour gear (XA Database)** option checks current-character ownership for equippable loot. Native inventory/registration and valid XADB inventory, retainer, armoire and glamour-dresser rows establish ownership. Empty, stale or incomplete storage responses retain the selected base mode: the current item API cannot certify complete storage absence. Live Greed/Pass availability still caps the result. XADB storage must be recorded and refreshed for the current character.

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

## Safety And Stop Guidance

- **Stop** is always available in Main and compact Controls.
- `/ads stop` releases ownership from chat.
- ADS can remain in observing mode without owning movement or progression.
- **Start Inside** and **Resume** require live instanced-duty truth.
- **Leave** is disabled unless ADS owns execution.
- Duty maturity and catalog metadata describe validation status; live instanced-duty truth controls whether inside start/resume is available.
- On an ADS-owned run, ADS uses XA Slave's dialog/cutscene skipper only when `TextAdvance.IsEnabled` is false or unavailable. `/ads skipper [on|off]` controls that fallback for the current run only; it never changes TextAdvance or FrenRider.
- ADS is inactive in the current crowded-activity territory list (512, 514, 515, 624, 625, 656, 732, 763, 795, 827, 900, 901, 920, 929, 939, 975, 1163, 1252, 1346): it parks existing work, refuses automation starts, and keeps the UI, **Stop**, and Cancel Utility available.

## Regular-Duty BossMod Follow

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
| Rules Editors | Executable inherited object-rule presets, protected `DEFAULT` shards, searchable multi-context filtering/revert/promotion, responsive compact editing, dialog presets, partial imports, bulk editing, and duty deep-links |
| Specialist Tools | Object, ghost, frontier, event, VFX, Higher/Lower, treasure, loot, reflection |

`/ads rules` keeps preset selection, draft actions, filters and selection controls
above the rule grid, whose editor rows meet at their dividers and retain inline controls.
Expand **Advanced**, then choose Preset management, Sharing, Draft editing,
PR-ready checkout or Guided Setup. Each tab shows only its related tools.
Edits still require **Save**; the row **X** uses the same draft deletion and **Undo** flow.

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

Settings > Automation > **Disable AutoDuty on ADS start** defaults off. Leave
it off to keep AutoDuty loaded and choose which plugin executes each duty.
Use the manual **Enable AutoDuty** and **Disable AutoDuty** buttons in `/ads mini`
when you want to change its loaded state. These actions do not change that preference.

- [Discord](https://discord.gg/ac6gjDvR8R)
- [Repository](https://github.com/McVaxius/ADS)
- [Ko-fi](https://ko-fi.com/mcvaxius)

When XA Slave is loaded, **Open XA Slave log tools** opens its **Utility > XA Mods** panel, which contains Dalamud Log Cleaner. The existing **Copy / ZIP Dalamud log** action remains separate. Opening the panel does not run cleanup or change XA Slave settings.
