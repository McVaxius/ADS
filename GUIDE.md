# ADS Guide

## Duty-Object Rules

- Rules live in `ADS/duty-object-rules.json` for the bundled copy and in the active Dalamud profile config for runtime edits.
- Lower `Priority` wins.
- Equal priorities do not automatically force a required interactable over the best live monster. In that case ADS falls back to distance and Y-space heuristics.
- `PriorityVerticalRadius` and `MaxDistance` act as rule gates for normal monster and interactable rules.

## Scope Fields

- Leave `DutyEnglishName` blank, `TerritoryTypeId` at `0`, and `ContentFinderConditionId` at `0` for a truly global rule.
- If `DutyEnglishName` is non-empty, ADS treats it as duty scope, not freeform notes text.
- Duty name matching is article-tolerant for leading `The`, but it is still a real duty-name match.

## Treasure Coffers

- A global treasure clamp rule should usually be scoped with blank duty and zero ids.
- Example pattern:
  - `ObjectKind`: `Treasure`
  - `ObjectName`: `Treasure Coffer`
  - `Classification`: `Expendable`
  - `PriorityVerticalRadius`: `5`
  - `MaxDistance`: `20`

## Map XZ Destinations

- Use `Classification: MapXzDestination` together with `DestinationType: MapXZ`.
- `MapCoordinates` should be player-facing map coordinates such as `11.0,11.1`.
- ADS converts those map coordinates into world X/Z on the live current map row and uses the current player Y for navigation.
- ADS prefers map-flag navigation with `/vnav moveflag` for manual Map XZ points and falls back to direct `/vnav moveto` if map-flag placement fails.
- If ADS hits `Svc.Condition[BetweenAreas]` while a manual Map XZ point is the current or last valid target, it ghosts that point immediately instead of waiting for a 1y X/Z arrival check.
- The active target's resolved world position is exposed in the status / analysis JSON as `frontier.currentTargetPosition`.

## Frontier Labels

- ADS now resolves the active sub-area by live `MapId` and only loads frontier labels from that map row.
- This prevents multi-submap duties from mixing labels across sections of the same territory.

## Current Limitation

- `PriorityVerticalRadius` and `MaxDistance` are not yet enforced for `MapXzDestination` rows.

## Dialog Yes/No Rules

- Dialog rules live in `ADS/dialog-yesno-rules.json` for the bundled copy and in the active Dalamud profile config for runtime edits.
- These rules are global `SelectYesno` prompt matches. They are not duty-scoped.
- ADS only applies them while it owns a supported duty, so they do not become a global background yes-clicker.
- `MatchMode` supports `Contains` and `Exact`.
- `Response` supports `Yes` and `No`.
- The bundled starter rule matches `imperial identification key to deactivate the barrier` and clicks `Yes`.
