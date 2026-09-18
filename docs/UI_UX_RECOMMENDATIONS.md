# AI Duty Solver UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Let an operator start or resume the correct duty behaviour, understand what ADS is trying to do, and separate normal operation from authoring and diagnostics.

## Reviewed surfaces

- `ADS/Windows/MainWindow.cs`
- `ADS/Windows/QuickControlWindow.cs`
- `ADS/Windows/ConfigWindow.cs`
- `ADS/Windows/WizardWindow.cs`

## What is already working

- The main window separates Overview, Duties, Tools, and Diagnostics.
- Duty, ownership, execution phase, planner objective, warnings, and catalog maturity are all observable.
- The quick-control window provides compact actions and contextual disabling.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Lead with a human-readable objective. | Translate ownership, phase, ghosting, planner, and catalog state into `ADS is moving to the next pack`, `Waiting for party`, or `Observing only`, with raw values in details. |
| P0 | Replace Start Outside/Inside/Resume with one contextual primary action. | Choose the correct label and behaviour from current duty/ownership state; keep Leave and Stop separate and clearly destructive. |
| P0 | Separate operation from authoring. | Put Loot, Rules, Objects, Dialogs, Maturity, explorers, JSON, and debug strafe under Authoring/Developer tools so the normal operator surface stays focused. |
| P1 | Explain disabled and observing states at the button. | For every unavailable Start, Resume, Leave, update, or tool action, expose the exact reason without relying on a tooltip or warning elsewhere. |
| P1 | Define duty maturity visually. | Give each maturity level a short definition, confidence indicator, known limitations, and recommended operator expectation in the Duties table/detail pane. |
| P1 | Make Guided Setup produce a verified outcome. | Each flow should end with a readiness check or configured setting, not only pages of guidance and copied commands. |
| P2 | Show immediate-save feedback. | Keep `Configuration saves immediately`, but add a subtle Saved/Failed indicator after changes and cache updates. |

## Suggested information hierarchy

1. Human objective and primary action
2. Current duty/readiness
3. Planner detail
4. Duty catalog
5. Authoring and diagnostics

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
