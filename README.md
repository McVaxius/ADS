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
- Seed active support only for the first simple ARR pilot wave: Tam-Tara, Toto-Rak, Brayflox, and Stone Vigil.
- Keep harder duties visible but non-actionable until their duty-profile overlays are written.
- Treat monsters as the default objective, with marked progression interactables allowed to outrank monsters when they are materially closer.
- Status colors in the catalog:
  - Red `[Not Cleared]`
  - Blue `[1P Unsync Cleared]`
  - Yellow `[1P Duty Support]`
  - Green `[4P Sync Cleared]`

## Current Validation

- The Tam-Tara Deepcroft is marked `[1P Unsync Cleared]`.
- The Praetorium and Castrum Meridianum are on the planned test list.
- ADS stops owned execution and clears recovery memory when Dalamud reports `DutyCompleted`.
