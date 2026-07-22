# AI Duty Solver

AI Duty Solver (ADS) is an operator-controlled Dalamud plugin for observing, planning, and executing supported instanced-duty routes. It exposes live duty truth, planner decisions, execution phases, authored object/dialog rules, treasure state, and specialist diagnostics.

ADS is observer-first. Entering a duty does not automatically grant ADS ownership unless the operator starts or resumes execution.

## Desynthesis

ADS includes policy-driven desynthesis with local presets, skill-up filtering, three source scopes, completed-duty gain tracking, and stable IPC. Open it with `/ads desynth`; Main > Tools > Treasure And Operations has compact Desynth Controls and Extract Materia launchers. See [Desynthesis](docs/DESYNTHESIS.md) and [IPC](docs/IPC.md).

## Shop Purchasing

ADS can resolve supported vendor offers and NPC placements from local game data plus a checked-in offline fallback catalog, then buy an exact additional item quantity with `/ads shop <itemID> <quantity>`. V1 supports regular gil shops and single-output `SpecialShop` exchanges paid entirely with inventory items or tomestones. Safe vendor fallback never changes the requested item, currency identities, or exact total cost. See [Shop Purchasing](docs/SHOP_PURCHASING.md), [Commands](docs/COMMANDS.md), and [IPC](docs/IPC.md).

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

Use **Resume** after a plugin reload or intentional stop while still inside the duty. Use **Leave** only when ADS owns execution and you want ADS to request duty exit.

## Safety And Stop Guidance

- **Stop** is always available in Main and compact Controls.
- `/ads stop` releases ownership from chat.
- ADS can remain in observing mode without owning movement or progression.
- **Start Inside** and **Resume** require live instanced-duty truth.
- **Leave** is disabled unless ADS owns execution.
- Duty maturity and catalog metadata describe validation status; live instanced-duty truth controls whether inside start/resume is available.

## Regular-Duty BossMod Follow

**Settings > Automation > Enable BMRAI/VBM in regular duties** is enabled by default. When enabled, ADS resets BMRAI and VBM follow targets to `Slot1` on the next regular-duty entry. Disable it to skip only those regular-duty Slot1 commands; treasure opener follow and treasure-exit cleanup are unchanged.

## Feature Map

| Surface | Purpose |
|---|---|
| Main > Overview | Operator truth: duty, ownership, phase, objective, explanation, warnings, options, status |
| Main > Duties | Searchable responsive duty dashboard with family filters, maturity cards, compact catalog, selected-duty details, and collapsible rule coverage |
| Main > Tools | Authoring, treasure, desynthesis, diagnostics, settings, updates, support links |
| Main > Diagnostics | Territory/map/CFC, frontier, treasure follow, observations, JSON evidence |
| Settings | General, automation, data/rules, advanced display, about/support |
| Compact Controls | Full-label primary actions, tool shortcuts, concise live status |
| Rules Editors | Live `DEFAULT` rules plus parked presets |
| Specialist Tools | Object, ghost, frontier, event, VFX, Higher/Lower, treasure, loot, reflection |

## Documentation

- [Operator Guide](GUIDE.md)
- [Commands](docs/COMMANDS.md)
- [Shop Purchasing](docs/SHOP_PURCHASING.md)
- [IPC](docs/IPC.md)
- [Rule Authoring](docs/RULE_AUTHORING.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Changelog](CHANGELOG.md)

## Support

- [Discord](https://discord.gg/VsXqydsvpu)
- [Repository](https://github.com/McVaxius/ADS)
- [Ko-fi](https://ko-fi.com/mcvaxius)
