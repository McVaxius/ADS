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

## Expendable Interactables

- Once ADS sends an interact to an `Expendable`, it keeps that object in follow-through instead of immediately marking it used.
- If the object is still live after the settle window, ADS retries it from the same `<1y` X/Z `moveto` stand-off until the object disappears.

## Required Interactables

- Once ADS sends an interact to a `Required` interactable, it now holds still and retries up to `3` sent attempts before releasing that retry window.
- If `Svc.Condition[BetweenAreas]` starts during that required-interaction follow-through, ADS ends the retry window immediately.

## Map XZ Destinations

- Use `Classification: MapXzDestination` together with `DestinationType: MapXZ`.
- `MapCoordinates` should be player-facing map coordinates such as `11.0,11.1`.
- ADS converts those map coordinates into world X/Z on the live current map row and uses the current player Y for navigation.
- ADS prefers map-flag navigation with `/vnav moveflag` for manual Map XZ points and falls back to direct `/vnav moveto` if map-flag placement fails.
- ADS does not pre-ghost manual Map XZ points during the background frontier scan; the point stays live until execution reaches the 1y X/Z arrival rule or `BetweenAreas` fires.
- Once ADS has selected a manual Map XZ point, it keeps following through on that remembered target even if live monsters or interactables appear before the X/Z arrival rule is met.
- ADS does not select fresh frontier or manual Map XZ targets while unsafe transition flags are active; it waits for post-transition truth to settle first.
- If ADS hits `Svc.Condition[BetweenAreas]` while a manual Map XZ point is the current or last valid target, it ghosts that point immediately instead of waiting for a 1y X/Z arrival check.
- The active target's resolved world position is exposed in the status / analysis JSON as `frontier.currentTargetPosition`.

## Recovery Ghosts

- Rule-backed interactable ghosts are recovery-only hints. They should not beat live monsters, live progression interactables, live follow anchors, or available frontier / Map XZ routes.
- If you see a rule-backed interactable ghost selected while those stronger live signals still exist, that is a bug.

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
