# Automated Four-Player Duty Testing

## Purpose

ADS will run a simple, sequential testing workflow for duties designed for four players. The workflow stays inside ADS; it does not require an external game agent. Each run uses the existing duty-specific rules and global rules. ADS records the result and, when a run does not clear, records where progress stopped and a short reason in the duty notes.

The planned result states are:

- `NotCleared`
- `Failed`
- `OnePlayerUnsyncCleared`
- `OnePlayerDutySupport`
- `FourPlayerSyncCleared`

`Failed` means that a test attempt did not clear the duty. It is a test result, not proof that the duty is permanently impossible.

## Level 1: One Player, Unsynced

Level 1 tests four-player duties with one real player running unsynced.

- Exclude Dawntrail duties from this level.
- Skip duties already marked `[1P Unsync Cleared]`, `[1P Duty Support]`, or `[Synced Party Cleared]`.
- Process the remaining duties one at a time using the existing ADS and global rules.
- If the duty is not cleared after 15 minutes inside the duty, end the attempt and assign the planned `Failed` result.
- Record the failure location, current objective or phase, and a short reason in the duty notes.
- Use those notes to hand-tune the relevant rules before a later rerun.
- A successful run can be promoted to `[1P Unsync Cleared]`.

## Level 2: Duty Support

Level 2 tests four-player duties with one real player and three Duty Support NPCs under the duty's normal synced conditions.

- Include eligible Dawntrail duties.
- Process each eligible Duty Support duty sequentially using the existing ADS and global rules.
- A completed run can be promoted to `[1P Duty Support]`.
- For an uncleared run, record where progress stopped and the short failure reason in the duty notes for manual rule tuning.
- No additional automatic timeout is defined here; Level 1's 15-minute threshold applies only to Level 1.

## Level 3: Four Real Players, Synced

Level 3 tests four-player duties with a complete party of four real players, synced to the duty's normal level and item-level rules.

- Include eligible Dawntrail duties.
- Process each eligible duty sequentially using the existing ADS and global rules in the real-party environment.
- A completed run can be promoted to `[Synced Party Cleared]`.
- For an uncleared run, record where progress stopped and the short failure reason in the duty notes for manual rule tuning.
- No automatic timeout is defined here; the party-run procedure determines when the attempt is ended.

## Boundaries

This plan covers four-player duties only. Trials, raids, alliance raids, and other content with a different party size are out of scope. The document describes the planned plugin workflow; it does not implement the runner, status model, persistence changes, UI, or automation.
