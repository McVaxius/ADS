# ADS Commands

ADS registers `/ads` plus aliases `/aids` and `/aisolver`. Commands are case-insensitive. An empty or unknown argument toggles Main.

## Desynthesis

| Command | Action |
|---|---|
| `/ads desynth` | Open desynthesis controls |
| `/ads desynth run configured\|all\|whitelist\|last-duty\|skillups` | Start policy-driven desynthesis using configured source scope |
| `/ads desynth run inventory-only` | Desynthesize inventory equipment directly |
| `/ads desynth run everywhere-skip-gearsets` | Desynthesize inventory and armoury items while protecting gearsets |
| `/ads desynth run everywhere` | Desynthesize inventory and armoury items, including gearset items |
| `/ads desynth stop` | Stop active ADS utility |
| `/ads desynthfrominventory` | Alias for `inventory-only` direct desynthesis |

## Windows

| Command | Action |
|---|---|
| `/ads` | Toggle Main |
| `/ads config` | Open Settings |
| `/ads mini` | Toggle compact Controls |
| `/ads obj` | Toggle Object Explorer |
| `/ads ghosts` | Toggle Ghost Inspector |
| `/ads labels` | Toggle Frontier Labels |
| `/ads rules` | Toggle Object Rules editor |
| `/ads dialogs` | Toggle Dialog Rules editor |
| `/ads loot` | Toggle Loot Controls |
| `/ads hl` | Toggle Higher / Lower window |
| `/ads higherlower` | Alias for `/ads hl` |
| `/ads higher-lower` | Alias for `/ads hl` |
| `/ads treasure` | Open Treasure Routes editor |
| `/ads events` | Toggle Server Event Explorer |
| `/ads mapeffects` | Alias for `/ads events` |
| `/ads vfx` | Toggle VFX Explorer |
| `/ads reflection` | Toggle BMR Reflection controls |

## Window Placement

| Command | Action |
|---|---|
| `/ads ws` | Reset all ADS window positions to `1,1` |
| `/ads j` | Jump ADS windows to visible random positions |

## Duty Ownership

| Command | Action |
|---|---|
| `/ads outside` | Queue outside ownership |
| `/ads inside` | Claim ownership inside current instanced duty |
| `/ads resume` | Resume ownership inside current instanced duty |
| `/ads leave` | Request leave state; nearby eligible chests may be handled before exit |
| `/ads stop` | Drop ADS ownership immediately |

Solo duties show the `/ads leave` recovery reminder once per stable entry. The reminder does not grant ownership or change the command's existing eligibility rules.

## Reusable Utility Automation

Utility automation cannot start while ADS owns active duty execution or while inn-entry automation is running.

| Command | Action |
|---|---|
| `/ads enterinn` | Move to a nearby innkeeper and enter the inn |
| `/ads shop <itemID> <quantity>` | Buy an exact additional quantity from a supported sheet-resolved vendor |
| `/ads repair self` | Start self-repair |
| `/ads repair npc` | Start NPC repair with normal fallback behavior |
| `/ads repair npc-no-inn` | Start NPC repair without inn fallback |
| `/ads repair npc-no-teleport-no-inn` | Use nearby NPC repair only; no teleport or inn fallback |
| `/ads selfrepair` | Open self-repair and repair equipped gear |
| `/ads npcrepair` | Move to a nearby repair NPC and repair equipped gear |
| `/ads npcrepair noinn` | NPC repair without inn fallback |
| `/ads npcrepair no-inn` | Alias for `/ads npcrepair noinn` |
| `/ads npcrepair-no-teleport-no-inn` | NPC repair only when a mender is within `120y` |
| `/ads extractmateria` | Extract ready materia from gear |
| `/ads desynthfrominventory` | Desynthesize inventory equipment directly |

Running `/ads repair` without a mode prints valid modes.

Shop item IDs and quantities must be decimal integers. Item ID must be a positive `uint`; quantity must be `1` through `9999`. Quantity means additional item units, not transactions and not a target inventory count. Deterministic gil, special/FATE, Inclusion, Grand Company, and Free Company families are eligible; turn-in, sale, lottery, ambiguous, or unprovable offers fail closed. Missing, extra, non-decimal, or out-of-range arguments are rejected without starting automation. See [Shop Purchasing](SHOP_PURCHASING.md).

Main > Tools > Treasure And Operations also exposes compact **Desynth Controls** and **Extract Materia** launchers. **Extract Materia** starts the same no-configuration automation as `/ads extractmateria`.

## Loot

| Command | Action |
|---|---|
| `/ads lootoff` | Set loot rolling mode to Off |
| `/ads lootneed` | Set loot rolling mode to Need |
| `/ads lootgreed` | Set loot rolling mode to Greed |
| `/ads lootpass` | Set loot rolling mode to Pass |
| `/ads lootregon` | Enable Need for missing registrable items |
| `/ads lootregoff` | Disable Need for missing registrable items |

## Treasure Follower Monitor

| Command | Action |
|---|---|
| `/ads td-monitor-on` | Arm treasure-follower duty-exit cleanup monitor |
| `/ads td-monitor-off` | Disarm treasure-follower duty-exit cleanup monitor |

## Debug Strafe

Debug strafe controls appear in compact Controls only while enabled.

| Command | Action |
|---|---|
| `/ads debug` | Print debug-strafe status |
| `/ads debug status` | Print debug-strafe status |
| `/ads debug on` | Enable debug-strafe controls |
| `/ads debug off` | Disable controls and release held input |
| `/ads debug release` | Release held input without disabling controls |

Closing compact Controls also releases held debug-strafe input.

## Higher / Lower Diagnostics

| Command | Action |
|---|---|
| `/ads hldebug` | Queue diagnostic snapshot |
| `/ads hldebug dump` | Queue diagnostic snapshot |
| `/ads hldebug state` | Queue focused state probe |
| `/ads hldebug trace [seconds]` | Start bounded trace; omitted duration uses default |
| `/ads hldebug export` | Export current texture probe |
| `/ads hldebug exportpath <tex> [u v w h]` | Export texture path, optionally cropped |
| `/ads hldebug card <1-9> [current\|next\|previous]` | Tag known card |
| `/ads hldebug board <left> <right> [label...]` | Tag known board; card token is `1-9`, `blank`, or `unknown` |
| `/ads hldebug solver` | Print solver state |
| `/ads hldebug status` | Print diagnostics/file/datamine status |
| `/ads hldebug folder` | Open diagnostics folder |
| `/ads hldebug on` | Enable Higher / Lower diagnostics |
| `/ads hldebug off` | Disable Higher / Lower diagnostics |

`exportpath` accepts either one path argument or one path plus four crop values.

## Higher / Lower Automation

| Command | Action |
|---|---|
| `/ads hlauto` | Print guarded automation status |
| `/ads hlauto status` | Print guarded automation status |
| `/ads hlauto on` | Enable guarded automation |
| `/ads hlauto off` | Disable guarded automation |
