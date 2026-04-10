# AI Duty Solver
---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

[XA and I have created some Plugins and Guides here at -> aethertek.io](https://aethertek.io/)
### Repo URL:
```
https://aethertek.io/x.json
```

---

[Join the Discord](https://discord.gg/VsXqydsvpu)

Scroll down to "The Dumpster Fire" channel to discuss issues / suggestions for specific plugins.

## Plugin Concept

- Show a real Lumina-backed catalog of all 4-man dungeons in the ADS main window.
- Keep the implementation honest: staged execution, passive observation, planner explanation, ownership controls, IPC surfaces, and live movement/interaction only where currently validated.
- Seed active support only for the validated ARR pilot wave: Tam-Tara, Toto-Rak, Brayflox, Stone Vigil, Aurum Vale, and Castrum Meridianum.
- Keep harder duties visible but non-actionable until their duty-profile overlays are written.
- Treat monsters as the default objective, with marked progression interactables allowed to outrank monsters when they are materially closer.
- Status colors in the catalog:
  - Red `[Not Cleared]`
  - Blue `[1P Unsync Cleared]`
  - Yellow `[1P Duty Support]`
  - Green `[4P Sync Cleared]`

## Current Validation

- The Tam-Tara Deepcroft is marked `[1P Unsync Cleared]`.
- The Thousand Maws of Toto-Rak, Aurum Vale, and Castrum Meridianum are marked `[1P Unsync Cleared]`.
- The Praetorium is on the planned test list.
- ADS stops owned execution and clears recovery memory when Dalamud reports `DutyCompleted`.
- When the object table goes empty mid-duty, ADS can promote the next unvisited map-label frontier point instead of backtracking through stale ghosts.
- The frontier label inspector reads the `MapMarker` collection referenced by `Map.MapMarkerRange`; this is required for Toto-Rak, where the territory map row and marker collection row are different.
- Automation now also uses those `MapMarkerRange` labels as frontier waypoints when the older level-backed label join fails, so Toto-Rak should prefer labels like `Abacination Chamber` over synthetic heading scouts.
- Frontier labels, fallback map flags, and manual `MapXzDestination` conversion now use the live sub-area `MapId` instead of mixing every map row in a territory, so multi-submap duties such as Keeper should stay inside the current subsection.
- Label frontier navigation now places an in-game map flag and sends `/vnav moveflag`, falling back to direct `/vnav moveto` only if map flag placement fails.
- ADS continues tracking visited frontier labels while live objects are present, but it only exposes a current frontier target once the live monster/interactable list is empty.
- Duty-object rules now auto-reload while ADS is running; `Ignored` can suppress `BattleNpc` rows such as Cid, and used progression interactables are suppressed by stable duty/object/position until duty reset or a large relocation.
- Dialog yes/no rules now auto-reload while ADS is running; these are global `SelectYesno` prompt matches that only fire while ADS owns a supported duty.
- `Required` BattleNpc rules make ADS seek/kill by configured priority before distance tie-breaks.
- `Required` progression interactables only hard-override monster-first flow when their effective rule priority actually beats the best live monster; equal priorities fall back to the distance and Y-space heuristics.
- `Follow` rules are BattleNpc-only and turn live NPCs such as Cid into live-only movement anchors that yield to real monsters/interactables and never become ghost targets; non-BattleNpc Follow rows are migrated to `Ignored`.
- Treasure coffer execution is sticky once ADS commits to the coffer, so a coffer that legitimately wins planning should be approached and handled instead of bouncing back to a monster objective mid-route.
- `MapXzDestination` rules create manual no-live-object waypoints from player-facing map coordinates such as `11.3,10.4`; ADS converts those to world X/Z with the current player Y, prefers map-flag navigation with `/vnav moveflag`, falls back to direct `/vnav moveto`, and ghosts the waypoint once it reaches within 1y on X/Z or when `BetweenAreas` fires during the area handoff.
- Fallback map-label frontier selection now prefers labels ahead of the current route heading instead of raw sheet order.

## Rule Guide

- See [GUIDE.md](GUIDE.md) for duty-object rule authoring notes, dialog yes/no rules, global scope behavior, treasure clamp examples, and `MapXzDestination` usage.
