2026-10-23 - Approved LCARS appearance

- Add LCARS to the existing Auto/Picker editor, with segmented window rails, framed panels, outlined retained actions and readable selected Settings navigation. Preserve duty ownership, tool behavior, native IDs and all sixteen languages, including Traditional Chinese.
- Share one saved default-muted UI tone setting between Settings, Main and titlebar speakers; keep the original Picker RGB values and dispose only the owned player. Pin existing CI to the published AethertekUI baseline. Version remains 2.0.0.2.
- Offline Debug/Release builds passed with two existing diagnostic warnings; all 26 focused native callback, guide, configuration and catalog tests passed. Full-window locale/layout, game/GPU and audible-output acceptance remain separate.

2026-10-23 - Return to center during Dragon's Neck

- During owned Dragon's Neck combat, turn off available BossMod AI providers and move to the arena center at (-258.08, 19.07, 18.11) immediately and every eight seconds. This takes priority over ordinary combat movement holds and leaves the independent rotation provider running.
- Stop the owned movement on combat end, Stop, disable, transitions, duty completion and unload. Preserve other duties and unowned battles. The user completed this fight manually; replay is skipped for this run.
- Validation: isolated Debug/x64 compilation passed and all 15 focused execution cases passed, including eight-second cadence, failed-command spacing, direct cutscene cleanup, lifecycle cancellation and exclusion of unowned/other duties. No deployment, live replay or version change.

2026-10-23 - BMR load-distance configuration IPC (I507)

- Use existing BossMod configuration IPC to save a load-distance preference when BMR is the single identifiable provider, then verify the bound live config value. Let the provider notify persistence once; an attempted rejected, unreadable or uncertain IPC write cannot fall through to reflection. Keep the existing path for unavailable or ambiguous channels.
- Registry filtering still requires the existing native reflection adapter. All 146 focused config IPC, runtime contract, pause and follow tests passed with the complete Release/x64 plugin build. Native client acceptance remains unverified; no client, deployment or version action is included.

2026-10-22 - VBM runtime compatibility (I507)

- Read the live rotation manager from the published VBM TickService layout or the newer direct service registration. Share that exact runtime identity between interaction and Crystal Tower pauses; reject missing hosts, foreign types and replaced services without constructing provider objects.
- Keep existing IPC writes and guarded restoration. Offline contract and pause tests cover both layouts; loaded-client acceptance remains unverified. No version or deployment change.

2026-10-22 - Keep generated frontiers ahead of weak recovery hints

- Prefer an available map frontier over an unqualified remembered interactable. This prevents stale shortcut hints from pulling a duty run away from forward navigation. Keep authored destinations, rule-backed recovery, monsters and live interactions ahead of this fallback.
- Validation: all 14 focused priority cases passed on the 2.0.0.0 baseline. Native bridge navigation remains pending the repaired runtime reload.

2026-10-22 - Approved ADS window layout and density

- Present duty ownership and primary actions together; flatten overview cards and use separate Settings navigation with grouped appearance controls.
- Align Shop order/catalog rows and keep secondary item drafts, vendor and retainer details in the same Shop Lists window. Preserve saved purchase modes/currencies, row Save/refresh behavior and existing Test/Run gates.
- Tighten selected toolbars and editable lists, group companion/desynthesis controls and simplify guide headings. Keep all independent windows and the existing Rules grid; only reduce its Advanced wrapper padding.
- Native compilation, interaction and visual acceptance remain pending.

2026-10-22 - Retain the validated MGP exchange through purchase cleanup

- Close the same validated MGP exchange after its NPC target clears, so a completed Verminion minion purchase can proceed to inventory registration. Retain the native window and receiver identity only for the owned transaction; reject changed characters, foreign targets, hidden or replaced windows, an agent bound to another window and changed shop/item/cost data. Existing NPC and selector ownership checks remain in place.
- Native evidence identified the leftover Zu exchange and verified registration after one explicit test cleanup. The production cleanup change and isolated client deployment remain under verification; no unrelated ADS source is deployed by this change.
- Validation: 197 focused shop tests passed against the installed 1.3.0.1 source with the isolated cleanup repair, final agent/window guard and diagnostic marker. Native deployment and verification of the patched cleanup remain pending.

2026-10-22 - Crystal Tower native mechanic integration (I617, partial)

- Distinguish Bone Dragon's revival deaths from its verified director completion signal. Use observed comet and ice-cage positions/radii for Behemoth and Amon cover, retaining the player's actual floor. Scope Labyrinth handlers to their authored rooms and clear an owned Atomos target when it loses eligibility; retain four stable preferred pad holders until the supported lane clears.
- Add Syrcus protection/meteor/add gates and World of Darkness Doom cleansing, platform return, cast/cleave cone avoidance, Mini-before-belly handling and alliance cloud regions. Read the full native alliance roster for consistent tower assignments; incomplete rosters remain unknown. Current cast facing and actor facing stay separate native observations.
- Pause provider actions as well as movement during observed Pyretic, preserving original flags/preset names for guarded restoration in both supported providers. Require fresh readback, retain failed cleanup as a hold and discard only on explicit terminal cleanup; no setter retries or global transient clearing. Correct BossModReborn's movement-pause function binding and its assigned-value return semantics.
- Sequence World of Darkness approaches with authored XYZ and measured 3D arrival. Garm and Queen Scylla require their observed follow-up waves; interact with the native false chest to summon the clone. Scope Atomos, Cerberus and Cloud handlers to the player's current room, handle the stomach first, and target the Dragon's observed heads/Prominence ahead of ordinary attacks. Keep the final transfer held until an actual destination observation; no cross-floor navigation or completion is inferred from a stall, missing actor or transition.
- Restore a Crystal Tower movement pause before the native interaction owner changes AI/presets, avoiding overlapping saved provider state. Unverified restoration withholds the interaction.
- Retain a failed movement restoration across subsequent movement/interaction ticks until explicit cancellation discards it. Invalidate Bone Dragon completion and Bomb add deaths when the native encounter revives or changes identity.
- Add Labyrinth sequencing with full XYZ approaches and observed own-alliance Atomos pads. Track the opening Pools packs through their 24 exact native layout/species identities (8/10/6), current-attempt living observations and matched deaths. Missing, duplicate or mismatched actors do not clear a pack; replacements/revivals invalidate affected receipts.
- Separate placed shortcut coordinates from the player's approach waypoints in both route validation and preset matching. Dispatch through the existing interaction owner, retain movement settling and wait for measured landings; shortcut confirmation and final transfer activation remain unavailable.
- Capture the native current tether independently of ordinary targeting, including positive cleared state and unknown changing/unresolved state. Add Glasya main-floor targeting for Clockwork Wrights, Squires and Spanner-rooks; preserve an already intercepted tether while Fetters is observed. The Squire is 0xBDA, not Moonring Blade 0xBD8. Node linking and outer-platform routing remain unfinished.
- Extend both managed preset packages to 160 scoped actor/shortcut rows and translate the new status keys across all sixteen catalogs. Full three-raid travel/sequencing, rare Syrcus phases and Cerberus chain pickup/attachment remain unfinished. No complete raid progression or live clear is claimed.

2026-10-21 - Alliance scope validation and Crystal Tower groundwork (I617)

- Resolve the local alliance from the native party-group mapping, independently of the Party List window. Reject malformed nonblank alliance scopes during import, shard validation and save; preserve blank wildcard and A-G grammar. Prefer an alliance-scoped row over an otherwise equally specific wildcard row before applying priority.
- Release retained authored navigation and cardinal holds when their duty/alliance scope changes or becomes unknown, without consuming the interrupted route/hold. Keep the scope check ahead of force-march arrival handling.
- Add explicit SYNC/UNSYNC strategy metadata that survives personal copies, imports and shard reloads; legacy rules remain opted out. Join fresh native actors, party identities and existing EObjAnim events without confusing shared nonnetwork entity IDs or native EventState with animation state.
- Begin runtime Labyrinth integration for deterministic four-member pad assignments, cross-lane pad release, Thanatos buff eligibility, Vassago/Bomb eligibility and latched Ancient Flare retreat. Guard movement-only provider pauses, restore ordinary combat movement, release on shared recovery entry and stop owned paths on cancellation or owner changes. Hold ambiguous boss identities and require a post-cast clear before releasing Flare retreat. Complete three-raid movement, phase handling, interactions and published preset content remain unfinished; no unattended clear is claimed.

2026-10-21 - Nonmodal first-use guide offers (I616)

- Show the Rules and Object Explorer offers as ordinary titled windows, without dimming or blocking their parent tools. Preserve their native identities, translated actions, independent consumed/visited flags and owner-close cleanup. Escape dismisses only the focused offer, including while collapsed; opening the guide from its toolbar retires the offer without another save.
- Native interaction coverage exercises both tools and densities, parent clicks, Open guide, Dismiss, close, focused Escape, collapsed Begin/End and direct-toolbar guide visits. No game or GPU acceptance is claimed.

2026-10-20 - Independent UI colour choices

- Add the Auto/Picker colour editor to native titlebar Colour popups and Appearance settings. Auto retains ADS's existing accent palette; Picker saves independent Background, Controls and Text choices globally and retains them when returning to Auto. Translate the editor across all 16 UI languages while preserving language ordinals and existing save paths.

2026-10-19 - Allow five seconds for repair completion

- Increase only the existing Self and NPC post-submission completion delay from 1.4 seconds to 5 seconds. Restore the original repair flow, completion outcomes and durability reader; other UI delays and the overall utility/travel timeouts are unchanged.
- Remove the superseded durability-wait implementation, its build marker and its twelve added test cases/fixture. Preserve version 1.3.0.0 and unrelated ADS work. Verified the two-line production diff, restored test file and scoped whitespace check; unrelated tracked ADS diffs are unchanged. No new build or deployment was performed.

2026-10-19 - Persist active dialog-rule presets (I605)

- Selecting or creating a dialog preset now activates its saved rules and persists the choice across reloads. Saving the selected custom preset updates runtime immediately; disabled custom rules stay disabled.
- Watch and open the active dialog file in the editor and Settings. BotologyUpdates still refreshes only DEFAULT and reloads the selected runtime preset without replacing custom files or switching selection. A missing or invalid selected custom file leaves dialog rules inactive and reports the error; explicit deletion of the active custom preset selects DEFAULT.

2026-10-18 - Debug Return recovery and Settings organization

- Add the saved Restart from entrance / Resume post-Return choice, defaulting to Restart while debug mode remains off by default. Require a confirmed owned-duty death, exact localized native Return prompt, native revival with area transition and stable living state in the same character/duty before applying once. No callback-only or position-jump inference and no new Return accept loop.
- Restart clears ADS's transient movement/interaction, recovery memory, visited/frontier and consumed-rule state; Resume retains progress and clears stale immediate commitments. Preserve presets, saved settings, real dungeon progress, completed-duty and explicit exit intent. Stop, Leave, debug off, logout, identity change and unload cancel pending processing without reacquiring ownership.
- Organize Settings into General, Automation, Data & Rules, Debug, Appearance and About. Keep a visible inactive Return selector while debug is off; retain independent diagnostics, all existing actions/values and native IDs. Reuse existing debug command side effects for the UI master, group integrations/cleanup/treasure/dialogs and updates/presets/metadata, and put checkout preparation in its existing expandable section.
2026-10-18 - Rules and Object Explorer guidance (I603)

- Keep a small labeled Guide button visible in each tool's main toolbar, including compact mode. Offer Open guide or Dismiss once on the actual window-open lifecycle; close and Escape consume the offer. Store each tool's offer/visit state separately from wizard completion, preserving older configuration and replay/reset behavior.
- Reuse the existing guide window for task-based Rules help, its generated class/field reference, and dedicated Object Explorer guidance for filters, selection, distances/rule hits, RULE drafts, coordinates/flags, exports and manual actions. Add the concrete Crystal Tower preset page and managed-preset/personal-copy guidance. Guide navigation only opens help; it starts no automation. Translate the new controls/content across all 16 UI catalogs.
2026-10-18 - Published object-rule preset distribution (I601)

- Extend the existing territory index with explicitly declared published presets and canonical shard lists. Download and validate the whole package through the existing 24-hour/manual updater, reload affected rules and preserve the active selection. Folder names and shard paths are validated; a folder alone is not a distribution contract.
- Protect updater-installed presets across rule saves, import destinations, delete and revert, including Windows case/sanitized-name aliases. The Rules editor offers New Preset for personal copies. Preserve pre-existing personal name collisions and report them in update status; never infer ownership from the DEFAULT - prefix. Refresh only updater-owned content and retain unrelated custom copies/data.
- Prepare separate SYNC/UNSYNC Crystal Tower packages in BotologyUpdates with source-reviewed targeting and nearby Labyrinth shortcut rules across all three raids. The existing Operator Guide explains mode selection, ADS/Fren Rider/combat/DAD roles, manual alliance-mechanic requirements and personal copies. Native clear behavior remains unverified.
2026-10-18 - Activity ownership policy (I587)

- Fully exclude Triple Triad Battlehall territories 579, 940 and 941. PvP, other Gold Saucer minigames, Hall of the Novice and seasonal instances require a deliberate Start Inside/Resume request for the current instance; older ownership cannot carry over. Apply the same decision before cutscene and Phoenix recovery holds and before startup side effects.
- Add a searchable, read-only Activity policies view to Duty Manager with installed-sheet names, every CFC/territory combination, actual game content type and automatic/manual/IPC permissions. Preserve ordinary duties without catalog metadata, outdoor utilities and the Ocean Fishing local-sale exception.
2026-10-18 - Full-inventory loot passing (I595)

- Pass eligible pending loot when all four loaded main bags confirm zero empty slots, including stackable items and rows with missing item metadata. Apply this after collectible/glamour Need overrides while preserving native eligibility and unknown-capacity behavior. No new setting.

2026-10-18 - Preserve teleport route progress through boss cutscenes

- Hold owned execution and frontier state before duty housekeeping or observation can clear completed destinations. Retain the same duty's known identity when native fields disappear during a cutscene or transition, keeping destination keys stable and preventing the first teleport rule from becoming eligible again.
- Recognize WatchingCutscene78 alongside the other cutscene flags. Preserve remaining timer budgets, unfinished force-march commitments, Stop, and real zoning; do not change preset timing, debug-command activation semantics, saved settings, or release versions. Game acceptance remains user-operated.

2026-10-17 - Automatic inactivity protection (DDuck I598)

- Reset native inactivity timers automatically while ADS is enabled and the character is logged in, including while duty execution is stopped. Reuse the existing framework update; no saved setting or BMR dependency is added.
- Disabling ADS or unloading the plugin stops these timer updates. Duty ownership, Start/Resume behavior and version 1.2.0.2 remain unchanged.

2026-10-15 - Expansion Poetics gear examples

- Add editable Poetics gear examples for 21 combat jobs across ARR Ironworks, HW Shire, SB Scaevan, ShB Cryptlurker and EW Credendum. Choose Expansion and Job in Shop Lists, then Add gear example. Existing lists and separate relic/material examples remain intact; adding an example never starts purchasing.
- Target one of each available piece, including PLD shields and individually named Endwalker weapons. Use the intended stat-role accessories for ARR/HW, omit obsolete belts and include each unique ring once. Prices and vendor/menu selection continue to come from the game catalog.

2026-10-15 - Inventory movement and Auto Equip IPC

- Expose `ADS.StartGearMove(bool allUnprotected)` and `ADS.StartAutoEquip()` through the existing guarded manual action wrappers. Advertise them in capabilities and reuse shared utility status/cancellation. Document the developer signatures, movement scopes and accepted-versus-completed behavior beside equipment-sale IPC.

2026-10-15 - Continue equipment sales past failed items

- Match native HQ sale transaction IDs to the selected item's quality, preserving exact item, slot, quantity and vendor checks.
- Skip unavailable sale callbacks, items that changed or gained protection during the batch, and settled unsuccessful or unverified receipts. Keep trying the remaining frozen items without retrying a skipped callback or selling replacements. Report sold and skipped totals in the result and equipment-sale IPC; retain Stop and ownership of unfinished transactions.

2026-10-14 - Inventory utility mini and equipment selling

- Add `/ads inv` and an Inventory shortcut in ADS Mini. Group current-job Auto Equip, protected armoury moves, white-equipment selling, AutoRetainer-list selling, repairs, materia extraction and existing desynthesis/shopping controls. Share AethertekUI appearance and compact density, and translate controls, help and new utility statuses through all sixteen catalogs.
- Label each inventory/armoury/both equipment-sale button explicitly as white-only; keep Sell AR list separate.
- Add `/ads gear equip`, a separate `/ads gear move-all`, and `/ads sell` for the existing AutoRetainer-list handoff. Keep `/ads gear move` white-only. Verify recommended gear before updating the active saved gearset; keep moves protected and report partial completion when bags fill.
- Continue armoury moves through independently updated source and bag slots within the existing settlement window. Verify moved item data without comparing native pointers or padding; retain quality, materia, dyes and item-state checks, gear protections and full-bag stopping without replaying a move.
- Use the native container-transfer request and check its return value, then verify the empty source and exact destination item before advancing. Continue the full batch when a successful request exposes no matching pending-operation entry; do not mistake the absence of that entry for submission failure or replay an uncertain move.
- Wait for the owned equipment shop and context menu to initialize, recognize the native vendor after the ordinary target clears, and accept only confirmations tied to the exact submitted sale. Verify the removed item and gil result without replaying an uncertain callback; retain the separately accepted fishing sale handoff.
- Wait for the native item-sheet read after submitting an equipment sale before checking committed transaction fields. Keep exact confirmation/settlement checks and log the expected and actual fields when they disagree.
- Keep tracking a submitted sale when the game clears its action code during completion. Read its exact quote and verify both inventory and gil, including updates arriving on separate frames, within the existing settlement window before advancing to the next item.
- Continue equipment selling and owned cleanup on the normal Buyback tab after a sale, without switching tabs. Preserve unresolved transactions; report verified sold and remaining counts after a partial failure or cancellation.
- Expose equipment-sale preview, start, operation-matched status and cancellation IPC. Preserve manual preview preferences, gearset protection, explicit local-only travel and existing generic NPC-sale IPC.

2026-10-14 - NPC sale handoff and settlement

- Expose exact sale-start rejections without replacing another running operation. Keep the explicit local-only Ocean Fishing exception and existing duty/inn/character protections.
- Wait for AutoRetainer work and its sale UI to settle before reporting completion. Preserve whether a sale command was dispatched in terminal receipts so callers do not move against an unfinished seller after cancellation.

2026-10-14 - Pause owned timers during cutscenes

- Hold ADS-owned execution and frontier progression during either native cutscene flag. Release owned navigation and directional input, retain objectives and remaining timeout budgets, then resume those budgets after the cutscene. Timers started during the hold retain their full budget. Keep Manual Stop and real zoning authoritative, retain treasure passage evidence, and prevent new follower automove submissions during cutscenes.
- Keep external interaction-witness freshness and diagnostic timestamps on their native clock. Translate the hold status through all sixteen existing catalogs. Source/local and actual client verification remain separate.

2026-10-14 - Responsive shop catalog search (I565)

- Filter native item, vendor, shop, territory and normalized currency metadata before resolving offers. Preserve currency discovery, receive bundles and exact-currency validation. Run the Shop Lists window's read-only catalog search off the UI thread, apply results only to the originating preset/currency and keep purchase actions separate. Preserve existing DAD saved-list associations.

2026-10-14 - Owned Ocean Fishing NPC selling (I568)

- Permit explicitly requested local-only NPC selling aboard Ocean Fishing and settled post-voyage selling when the cached context still names Ocean Fishing. Keep every other activity exclusion and normal duty/inn/AutoRetainer, character, UI, cancellation and verified-sale checks. Tick only the owned sale inside the otherwise parked activity; no onboard travel or ordinary utility exemption.

2026-10-13 - Scope Higher/Lower diagnostics to treasure dungeons

- Capture Higher/Lower diagnostics only when enabled and inside a supported treasure dungeon. Keep live debug views, forced dumps and traces behind that gate; retain gameplay solver inputs independently.
- Default diagnostics off for new settings and reset existing diagnostic preferences off once through configuration schema 25. Preserve later saved choices. VFX datamining retains its separate opt-in setting and is also limited to treasure dungeons.

2026-10-11 - Performance investigation coverage (I544)

- Extend the existing opt-in hitch profiler to report ADS framework updates and UI drawing from 10 ms, retaining a five-second reporting interval for each. Include automation state and open window types so idle/active and UI-open/closed samples can be distinguished.
- Preserve default-off profiling, saved settings and automation behavior. Report slow early-return updates too; these diagnostics do not establish that the reported FPS defect is fixed.

2026-10-11 - Culture-safe coordinate copy (I538)

- Copy Object Explorer row XYZ coordinates with invariant decimal points so comma-decimal system locales retain three usable coordinate fields. Preserve localized display and existing map flag copy formatting.

2026-10-11 - Titlebar status and Object Explorer map flags

- Add a one-shot red map flag action to the Object Explorer map toolbar. Use the displayed zoom/pan transform for placement and cancel on Escape, right-click, window close, hidden map, or zone changes.
- Show current operational status in native window titles, with separate saved Appearance switches for visibility and detailed wording. Object Explorer shows object count and selected name; settings and auxiliary titles use the packaged plugin icon.

2026-10-11 - Appearance titlebar shortcuts (I532)

- Move Main's language, compact and transparency shortcuts into the native titlebar and add a colour palette shortcut. Other titled windows expose the same actions; retained Appearance settings and main-control visibility choices use the existing save path.
- Preserve existing title actions, window identities, geometry, complete-window opacity and release version. Popup ownership and collapsed-window actions remain subject to native and game verification.

2026-10-11 - Resize the Object Explorer map

- Drag the divider between the map and table to change the map width; double-click it to restore the original width. Retain the choice for this plugin session, preserve a usable table width and use the resized canvas for map drawing and marker clicks.
- Keep the existing initial map width and narrow-window behavior. Add the divider tooltip in every interface language.

2026-10-11 - Include all personal storage in loot ownership

- Include live NQ/HQ equipped and Armoury copies, plus the left and right normal/premium chocobo saddlebags, when deciding collectible and glamour ownership. XADB inventory rows cover all these recorded locations; retainers, armoire and glamour dresser also remain included. Keep native unique-item and roll-state limits unchanged.

2026-10-11 - Use recorded storage for missing loot

- Missing collectible/glamour overrides use native ownership plus the current character's recorded inventory, armoury, saddlebags, retainers, armoire and glamour dresser. No recorded copy counts as missing, including unrecorded storage; older recorded copies remain owned until refreshed. Preserve manual enablement and native live-roll limits.

2026-10-10 - Object Explorer map and tight table (I525)

- Add the approved optional game map beside the table, with shared filters, linked selection and a pinned row. Use the current map sheet's scale/offsets and original game texture; pan, zoom and center without starting a game action. Cycle overlapping markers and clear selection when its object, map or filters change.
- Hide the map at narrow widths, replace row actions with compact icons and translated tooltips, and expose session-only column choices. Keep all diagnostics under Diagnostics, retain flag XYZ/separate X/Z copying and every original action, and leave DNav teleport hidden until explicitly exposed. Preserve versions and saved settings.
- Measure the filter toolbar and complete Kind previews from current translated controls; let Kind, Columns and level comparisons wrap when their row is full. Keep hidden action captions from widening their icon columns.

2026-10-10 - Optional manual DNav teleport (I526)

- Add a hidden-by-default Object Explorer teleport column when compatible DNav status/request IPC is loaded. Resolve the live object again on click, keep DNav's filters and shared cooldown, show refusals, and never retry an unconfirmed result or enable a movement mode.

2026-10-10 - Flag coordinate copying (I527)

- Show the current flag's world coordinates in Object Explorer, including Compact mode. Click resolved XYZ to copy it for a rule; use the separate Copy X/Z button when only horizontal coordinates are needed. Keep unresolved height explicit and prevent old, cleared or unavailable flags from supplying stale XYZ.

2026-10-10 - Explicit missing-item loot policy (I519)

- Apply missing collectible/glamour overrides only to confirmed Missing evidence, then retain native live-roll caps. Unknown ownership preserves the configured mode; native lookup failures stay unknown.
- Keep supported XADB responses positive-only until complete storage absence is supplied. Empty storage never requests a missing-item override.

2026-10-10 - Compact defaults (I521)

- Apply Compact mode and hide main Compact/Transparency controls once. Appearance settings can restore the controls and change density; subsequent loads preserve those choices, unrelated preferences and unknown saved settings.

2026-10-10 - Current-character XADB loot ownership (I519)

- Accept supported XADB item responses and recorded armoire/dresser data for current-character ownership. Preserve configured loot behavior when storage absence is unproven, including empty or partial responses.

2026-10-09 - Separate XA Slave log-tools shortcut (I512)


- Add Open XA Slave log tools beside the existing manual support exporter when XA Slave is loaded. The new action opens Utility > XA Mods only; preserve the Copy / ZIP button, its handler and cap warning. No automatic cleanup, provider loading or settings changes.

2026-10-09 - Manual AutoDuty mini controls (I511)

- Add Enable AutoDuty and Disable AutoDuty to /ads mini through the existing companion-plugin actions. Include both translated labels in responsive width budgeting; keep the separate automatic-disable preference off by default.

2026-10-09 - Manual support log export and optional AutoDuty disabling (I506/I509)

- Keep AutoDuty loaded on ADS start by default. Add an explicit Automation preference for users who want ADS to disable it.
- Add a manual support log ZIP action in Settings > About using the current client's log and a shared-reader snapshot. Warn before exporting a log at or above the 100 MiB cap; open the output folder and leave sharing and cleanup manual.

- Prepare the whole-ADS compact pilot with smaller independent spacing, single-line run actions, inline summaries and compact first-use editor sizes. Retain rule selection, deletion, preset and sharing controls in compact mode; context/checkout controls expand under Advanced so rows remain visible. The frozen first column gains a red row-delete action with the existing Undo and explicit Save behavior. Native rendering and user visual acceptance remain separate.
- Wrap small toolbar actions and shop-list row editors when their complete labels no longer fit, retaining their native identities and purchase controls.
- Fit compact status columns to translated values and reserve button height for complete glyph ink, including descenders outside the font line box.
- Keep populated Shop List preview rows reachable when the controls above them exceed the window height; reserve a usable scrolling table height and fit each retained row editor label.
- Reorganize the Rules Editor around a compact action bar, aligned context/scope/search filters and a larger rule grid. Advanced separates preset management, sharing, draft editing, checkout and guidance into tabs that show one group at a time. Restore the established rule-row spacing and inline help/copy/paste buttons; fit dropdown values and classification help at the current density and text scale. Retain the compact toolbar, original control IDs, explicit Save and undo-aware row deletion.
- Reduce the Rules grid's horizontal cell inset to one scaled pixel so row editors sit close to their column dividers, retaining the approved row height and toolbar/Advanced layout.
- Remove vertical cell padding from the Rules grid so successive editor rows meet at the row dividers. Retain editor padding, text height, inline actions and the approved toolbar/Advanced tabs.

2026-10-08 - Dedicated Window appearance settings (I505)

- Move colour, language, compact mode and transparency controls into their own settings tab or sidebar page. Retain the existing controls, native IDs, saved preferences and actions; keep normal settings visible without an appearance block above them. Versions and client configuration are unchanged. Local build checks and game visual acceptance are recorded separately in the selected task checkpoint.

2026-10-08 - Gil vendor exit after purchases (I496)

- Finish regular gil shops through their native shop handler and agent, then close the returning owned NPC menu through the existing bounded cleanup. Wait for native transactions and confirmations to settle before closing.
- Retain cleanup ownership through the active native shop/selector NPC when the ordinary target is cleared; reject another character or unrelated interaction. Account 1 purchase of two Mimett Gourds for 696 gil is live confirmed; the user has accepted Chocobo purchasing and vendor cleanup as working.

2026-10-08 - Gil vendor menu selection (I496)

- Select the uniquely matched shop handler through EventFramework's native selector option instead of passing handler metadata to a visible menu callback. Keep character/NPC ownership, exact gil offer, price, capacity and acquisition validation. This corrects the reported Vath beast currency window when requesting the gil shop.
- Add a focused regression for different selector-slot and global indexes; let the existing test references honor the builder's DALAMUD_HOME override. Account 1 gil purchases are live confirmed and user accepted.

2026-10-08 - GitHub Actions shared-library repair

- Build against published AethertekUI main so current shared APIs are available. Retain repository-specific read-only SSH deploy keys, which do not expire, and disabled credential persistence. Publish library APIs before consumer changes.

# Changelog

## Unreleased - Local-only currency purchases

- Add optional boolean `allowTravel` to `ADS.StartCurrencyShopPurchase`; omitted values retain existing travel behavior. Explicit `false` prohibits teleport, aethernet transfers and approach navigation while allowing a verified nearby merchant or ADS-owned held shop through the existing purchase runner.
- Recheck local vendor reach before interaction and fallback, and retain exact currency, cumulative spend cap, ownership, live shop/row, inventory and confirmation checks. Focused source tests cover defaults, invalid flags, local-only paths and purchase guards; compilation, test execution and game acceptance remain pending.

## Unreleased - Hindi font availability and recovery

- Treat the Hindi language-menu caption as optional while retaining mandatory selected/English catalogue checks. Refresh the stable Hindi option's availability with the existing font generation; unavailable captions use disabled ASCII `Hindi (unavailable)` without blocking ordinary languages.
- Keep font-failure status readable in ASCII and offer an explicit Use English action for selected Hindi through the existing configuration save route. Retain atlas roles, merges, dimensions and native control IDs.
- Source integration is complete; compilation, native availability/recovery checks and Linux/Wine acceptance remain pending.

## Unreleased - Original plugin images and UI guidance

- Replace Main's drawn emblem with the existing embedded plugin icon and add original-colour images to Main/Quick Controls titles, including collapsed windows. Retain existing body geometry, title text, native actions and saved window placement; borrowed host textures keep aspect ratio and a blank reservation while unavailable.
- Clarify shared appearance controls and this plugin's existing automation/setup ownership in the README.
- Source integration is complete; compilation, native image/title/control checks and game acceptance remain pending.

## Unreleased - VBM interaction hold (I466)

- Pause the captured VBM AI and active presets immediately before ADS interaction, retaining the exact original state for guarded restoration. Keep BMR enabled. Hold ADS movement and VBM through native interaction actions, casting and dialogs beyond the existing retry timer; discard restoration after stop, logout, transition or ownership/provider changes.
- Publish the transient VBM interaction hold in the existing status JSON so confirmed FrenRider and MOGTOME activation paths can defer until the interaction ends. No saved settings, new endpoint, dependency or generic lease service is added. Live acceptance remains user-controlled.
- Focused interaction, provider pause, duty context and recovery checks pass 113/113 with no failures or skips. The unchanged ADS launcher builds Debug/x64 with zero errors and two existing obsolete-distance warnings; the earlier dispatch-fixture failures are not a passing run.
## Unreleased - Button sizing

- Use local Toolbar metrics for ordinary buttons, growing from 30 pixels in Standard or 24 in Compact for the active font while retaining native labels, actions and small/dense control sizing.
- Current Debug/x64 compilation passes. The final ordinary-wrapper native route passes 9,329 checks in sixteen scenes with 96 pointer activations, exit 0. The separate icon route retains four height-expectation failures while its caption/icon containment, IDs, activation and restoration checks pass; its expectation adds padding around the retained icon geometry beyond the existing sizing rule. No full native-matrix pass or game acceptance is claimed.

## Unreleased - Managed CJK font atlas

- Merge one bundled CJK face per font role, selecting the active language's regional forms. Set both managed atlas dimensions to 4096 on every rebuild; preserve font heights, required glyph ranges, symbol merges and host-language coverage.
- Current compilation and guarded production callback/rebuild checks pass, together with bounded native glyph checks for the checked text. Managed-host readiness, complete displayed glyph coverage, language/scale host rebuilds and game/GPU acceptance remain unverified.

## Unreleased - Native titlebar shortcuts

- Add the approved Settings, Quick Controls, Start Outside, Start Inside, Resume and Stop shortcuts to Main, and Main, Settings and the same run actions to Quick Controls. Recheck current duty/activity eligibility before invoking the existing compound handlers, and show translated blocker/status feedback in title tooltips.
- Retain every body control, including Leave, tools and companions. Reserve native title/control width before motion preparation using logical constraints and the current UI scale. Debug/x64 compilation and focused descriptor guard checks pass; native rendering and game acceptance remain separate.

## Unreleased - White-name equipment cleanup

- Add `/ads gear move` to move eligible Armoury equipment into ordinary inventory, retaining remaining items when bags are full. Add `/ads gear sell armoury|inventory|both` with an exact item preview, Sell/Cancel, and one saved default-on confirmation preference in preview and Settings.
- Protect equipped items and all normalized gearset references, require white-name equipment and NPC-buyable items for selling, and revalidate the confirmed native slot before each operation. Use existing ADS vendor approach and the owned shop's exact inventory callback; stop on stale selection, safety changes or unsupported shop/confirmation state. Verify slot changes before reporting progress, and keep generic dialog automation suppressed until unresolved sale UI closes or the character changes. Focused checks and Debug/x64 compilation pass; game acceptance remains unverified.

## Unreleased - NPC selling handoff

- Find nearby sale vendors independently of repairs, including AutoRetainer's direct and wrapped gil-shop handlers. Reach the existing interaction distance and settle ADS-owned navigation/travel before the single AutoRetainer handoff, preserving repair behavior, local-only bounds and configured sell lists.
- Expose the actual sale status, require observed AutoRetainer work before accepting later idle, and report unobserved work or timeout. Preserve unrelated automation and dialogs on cancellation and ownership changes; remove the global AutoRetainer reset from sale cleanup. Translate the new statuses in all sixteen catalogs. Installed-version item-sale acceptance remains unverified.

## Unreleased - Community invite

- Update the current About/community link and README invite to `https://discord.gg/ac6gjDvR8R`. Preserve historical and reference links.

## Unreleased - LootGoblin completion sweep ownership

- Let an acknowledged LootGoblin duty-start handoff select a transient final-coffer sweep without duty exit through the existing operator API. Publish a duty-matched completion result only after ADS's existing sweep checks finish; preserve standalone and explicit manual leave behavior. Keep duplicate completion events from restarting an active sweep, and clear the handoff/result at ownership and duty boundaries.

## 2026-10-06 - Actions shared-library revision

- Pin the existing AethertekUI checkout to published commit 6c193cf06ac67f954c549cafc2033ac0efdd630a so fresh builds receive the Hindi shaping APIs required by this consumer. Preserve existing credentials, build/package paths and release behavior; GitHub execution remains pending.

## Unreleased - Hindi shaping integration

- Integrate the consumer-owned AethertekUI text renderer across retained windows and font-status paths. Route text, measurement, tooltips, controls, editors and expanded/collapsed window titles through shaped drawing while preserving native identities, title controls, saved values, nine font roles, configuration and automation. Reserve natural line height for Main/Settings tabs and two-line action tiles; retain tree/sorting arrows and restore modified tooltip and editor scopes on failures. Complete the 3,726-key Hindi catalog and append its locale without changing the fifteen existing ordinals.
- Preserve captured service details, leading-zero values and terminal newlines by using the existing complete-template matcher without its nonbacktracking option; the installed runtime returned empty named captures with that option. Bound each matcher to the established 20-ms consumer limit. Keep integer-input zero-step defaults and native flags through the current binding signature.
- Debug x64 compilation passes with zero errors and two existing explorer distance deprecation warnings. The bounded offline consumer probe passes 11,577 assertions over all sixteen exact embedded catalog key sets, Hindi values/placeholders/edge whitespace, nine original font roles and retained native controls, editors, tabs, tree/sorting and expanded/collapsed titles at both densities and 100%/150% scale. The final probe also checks the 20-ms matcher bound and finishes in 75.34 seconds with 81.09 MiB observed peak working set. Game, GPU, managed-font and IME acceptance remains separate.

## 2026-10-06 - Window appearance and transparency

- Move colour configuration into the existing Settings Window appearance section, retaining compact/language access there. Independently hide or show the main-window compact and language controls, both visible by default. Add the main Transparency toggle without changing automation actions or native control identities.
- Persist full-window opacity through the existing configuration: 100% normal opacity, automatic fade enabled, 50% unfocused opacity after 10 seconds. Clamp opacity to 10-100% and delay to 0-3600 seconds. Apply one shared opacity pass per owner after native drawing and motion restoration, including collapsed and font-status windows; retain native chrome, image alpha and owned child/pop-up content.
- Translate the eight new appearance labels in all 15 existing catalogs. Current-version compilation, focused persistence/focus checks and game acceptance remain pending for this source change.

## Unreleased - UI label corrections

- Remove unrelated second lines from the Italian Enabled glyph and ID labels and the Italian/Russian unknown fallback. Preserve resource keys, native controls and automation. The unchanged launcher build passes, and its delivered assembly passes 126,752 catalog/helper checks across all fifteen exact 3,718-key catalogs. Managed-host and game acceptance remain separate.

## 2026-10-05 - Rounded window chrome and native minimize (source adoption)

- Adopt per-window rounded chrome and animated native minimize/restore through the shared PositionedWindow and direct window lifecycle hooks, including font status. Preserve control identities, layout, saved geometry and actions, and retain LazyLootWarning's NoCollapse.
- Compilation, native interaction and game acceptance for this source adoption remain pending verification.

## 2026-10-05 - Formatted-number font coverage

- Include the selected UI culture's number-group separator in the existing required glyph set, including French narrow nonbreaking spaces in inventory and purchase quantities. Reuse that set for preparation and focused checks while preserving font roles, sources and merge order.

## 2026-10-04 - Client7 native reload verification

- The manual client reset restores the existing Dalamud log and loads marker14 at 18:13:32 from the native A: equivalent of the authorized R: development path. Source version remains 0.9.7.0. Existing guarded supply requests reject unavailable routes and insufficient currency; the complete 24-file fishing payload matches both deployed plugin folders by direct byte comparison. Add no independent logger or watcher.

## 2026-10-03 - Visible shop blockers

- Include the visible addon name in existing shop UI rejection status and logs. Keep guarded purchase authorization and confirmation checks, and leave pre-existing user dialogs untouched. Preserve source version 0.9.7.0 and public IPC contracts.
- Refresh the compiled fishing verification marker to marker14 for an isolated client7 Debug rebuild and DLL overwrite, checking the existing ADS reload path alongside VMX's unobserved reload. Add no reload transport or watcher.
- Marker14 builds as Debug x64 and is verified in the overwritten ADS DLL and combined 24-file payload at unchanged version 0.9.7.0. A manual VERMAXION reload subsequently loaded VMX13 and verified its automatic travel Stop, but ADS14 still has no matching native startup evidence. The later VMX15 overwrite also has no matching reload event through 23:18; native ADS14 verification remains pending.

- Place the complete ADS14 payload and dependencies under R:\xivlauncher7\FishCollectionClient7Verification\ADS for the requested client7 path migration. Update its existing development location and matching settings key while retaining plugin identity, automatic reload, source version 0.9.7.0 and public IPC. The combined 24-file payload matches the R: staging files; native adoption remains pending.

## 2026-10-03 - Exact-item vendor discovery

- Accept an optional itemId in the existing shop-catalog search request. Resolve only that item's offers for automated restocking, retaining unrestricted text/currency discovery when the field is omitted. This avoids resolving the entire catalog on the client framework thread for a known fishing supply.


## 2026-10-02 - Build and release repair

- Pin GitHub builds to SDK 10.0.201 and pass the downloaded Dalamud library path. Restore and build plugin projects with matching configuration, platform and runtime; stop on restore failure.
- Keep build tokens read-only and release writes in a separate job. Use packaged manifest versions for untagged releases.
- Local launchers build the plugin directly in the pinned environment and return its exit status.

## Unreleased - Native UI and localization

- Align the header and card headings with the approved references and size Overview label/value columns for each density, measuring complete translated labels to keep them on one line. Keep the execution status in a concise row and remove trailing label separators. Inset native tab and detail-column text to retain edge glyphs, wrap values within their native columns and remove the surplus final card gap. Match the Quick window caption in all fifteen existing catalogs while preserving resource keys, native IDs, configuration and source version 0.9.7.0.
- Build a registered status matcher only when its existing prefix is reached, retaining that matcher for the current template lifetime. Preserve template priority, nonbacktracking matching and nested/raw display behavior while avoiding allocation of every matcher on the first displayed service message.
- Append Vietnamese, Indonesian, Polish and Turkish with complete 3,718-key catalogues for authored controls and displayed service messages. Retain existing language ordinals, Brazilian Portuguese/Traditional Chinese, command and schema tokens, raw exception details, configuration version and automation behavior. Locally verify all fifteen embedded catalogues and nested statuses, selector IDs, saving and both densities at 100%/150%. Verify actual Main/Quick windows at 100% with all nine original font roles in a bounded 4096-by-4096 standalone atlas; managed-host font readiness and game screenshot acceptance remain open.
- Apply the approved regular/compact header, action bar, Overview cards and vertical Quick Controls with shared relative colour roles and measured managed-font roles. Keep existing action IDs, native language-picker identity and automation callbacks; size translated actions in their actual font roles, wrap Quick Controls status and expose clipped action text in tooltips. Format typed distances and settle times in the selected language.
- Show the unchanged assembly version in the main title. Reflow complete native fields, selectors, checkboxes and buttons with measured editor, preview and caption minima; retain caller-specific scalar steps and current native IDs. Add shared state/card/Quick Controls artwork and retain all eleven 3,718-key catalogs.
- Add Italian/Russian to the existing language choices and remember colour/compact selections through the current configuration path. Verify all eleven embedded catalogues at 3,718 nonempty keys with matching placeholders, including translated ownership, execution, planner and frontier values displayed by Main and Quick Controls. Local compilation is verified; native-client acceptance remains open.

- Refresh the main operator console with captioned actions, state summaries, and responsive Overview cards while retaining the existing controls and automation safeguards.
- Add nine UI languages with a remembered preference, client-language defaults, native Dalamud fonts, and translated settings, setup flows, warnings, and status displays. Commands, IPC, editable data, and raw diagnostics retain their original values.

## Unreleased - Phoenix Down recovery hold

- Check `FrenRider.PhoenixDown.ShouldPauseDutyProgression` before planning, owned movement, and objective actions. Stop ADS movement once on recovery entry, retain ownership and run state, and publish `ADS.IsPhoenixDownRecoveryHoldActive` after yielding movement.
- Resume planning from fresh observations after revival without a Stop/Resume cycle. Explicit operator Stop and Leave remain authoritative. Offline integration uses the real ADS and Fren Rider coordinators with synthetic four-client death, inventory, cast, and revival state; native cast/revival/resumption still requires mcvaxius-controlled live acceptance.

## Unreleased - Bounded currency shop purchases

- Add `ADS.StartCurrencyShopPurchase(string requestJson) -> bool` for bulk requests with an operation ID, item, quantity, exact currency identity and maximum currency spend. Enforce cumulative verified batch costs before purchase callbacks through the existing runner; preserve guarded single-purchase behavior. Accept the catalog's currency names and reject invalid currency item identities.
- Offline verification: 193 focused purchase, catalog, reliability and IPC checks pass. Prepare the requested Debug x64 collection payload as 0.9.4.4 without changing the repository's separately committed 0.9.5.0 version. Live collection integration remains pending.

## Unreleased - Guarded purchase handoff

- Match complete purchase-confirmation numbers independently of the machine's culture. Accept ungrouped prices and valid comma, period or nonbreaking-space thousands separators; reject partial amounts, malformed grouping and wrong quantities. Preserve owned-token, item-preview, expiry and single-use checks. Native validation completed one Wayward Hatchling purchase for 2,400 gil.
- Add opt-in Jonathas certificate claims to guarded minion purchases. Resolve his localized exchange entry, advance only owned dialogue, use the managed certificate balance, and require actual funds plus the caller's saved authorization before spending. Report observed claims separately from purchase receipts. Claims and exact two-certificate purchases of Wind-up Odin and Wind-up Cursor are verified.
- Route Jonathas visits through Gridania's main aetheryte and the Lancers' Guild shard, requiring actual arrival in Old Gridania before NPC navigation. Cancel all shop purchases on character changes.
- Select Jonathas's observed Others tab for minions, clear the recent-items filter, and verify the displayed item, global callback row and exact certificate cost before purchase. Native claims, travel and purchases are verified; an initially checked recent-items filter remains untested.
- Send node clicks to their registered receiver with a copied event and non-null event data. Native evidence confirms the category event exposes Others items without toggling the radio's visual flag; validate the displayed rows instead. Remove temporary category diagnostics after successful purchase verification.

- Continue single-purchase vendor cleanup for up to ten seconds when the shop returns to its parent menu. Require the same character and targeted NPC; retain the verified purchase receipt independently.
- Route MGP special shops through the existing currency-exchange UI adapter, validating the live item, price, callback row and owned confirmation before spending.
- Read MGP from InventoryManager's currency item balance; CurrencyManager reports no MGP and incorrectly rejected affordable Gold Saucer purchases.
- Classify literal Item 29 costs in Gold Saucer special shops as MGP, so guarded requests use the MGP balance and exact currency identity. Preserve ordinary item-token costs.
- Guard NPC sale cleanup against character changes, changed vendor targets and AutoRetainer work already becoming idle. ADS dismisses the sale confirmation with its tracked shop; the underlying AutoRetainer reset remains global.
- Route caller-owned NPC selling through ADS's existing mender travel flow and AutoRetainer's configured sell task. Support local-only onboard operation, observe busy-to-idle completion, and cancel only a matching request. Do not infer sale completion from gear repair status. Live validation remains pending.
- Add correlated company-action purchases to the existing shop runner. ADS owns quartermaster travel, exact action/rank/price validation, single-action confirmations, action-count and credit reconciliation, cancellation and shop cleanup. FC action inventory is distinct from item inventory; changed character/company, missing evidence and unknown results stop the request. Native validation remains pending.
- Wait for temporarily absent vendors within the existing five-minute run bound; begin the normal menu-opening timeout only after the NPC returns.
- Keep landing, dismounting and temporary NPC absence within the existing shop-opening timeout, without using up interaction attempts before an NPC command can be sent. Capture one bounded NPC availability diagnostic on failure.
- Accept correlated gil stock purchases through the existing runner, so callers can match their result and cancel only their own operation. Restrict these requests to gil offers and close their shop after completion.
- Expose the existing single-item checkpointed shop runner to callers that must save and approve an exact quote before submission and confirmation. Return the caller's operation ID and cancel only a matching active purchase; existing shop callers retain their behavior. Validation is in progress.
- Discover battle-NPC vendors through their sheet event-handler links and layer placements, including Nonoroon's Baby Bat shop. Use native flight for long vendor trips with existing flight unlocks; keep all navigation and shop verification in ADS. Live verification is pending.

## 2026-09-24 — Activity ownership and V&C loot

- Keep ADS effectively enabled, recover saved disabled settings on load, and retain the compatibility checkbox/IPC field. Log enable-field requests on both configuration IPC routes, including ignored disables and their caller stacks.
- Exclude every Palace of the Dead, Heaven-on-High, Eureka Orthos and Pilgrim's Traverse floor/rest territory through the activity gate. Release ownership there while keeping outdoor entrances, purchases, UI and IPC available.
- Classify content-type-30 V&C duties as FourMan with capacity four, preserving support/maturity settings. Keep ownership through their completion treasure/Personal Spoils sweep, then release without automatically leaving.

## 2026-09-18 — Relic orders (local-only, unpublished)

- Added editable ARR Zodiac and HW Anima Poetics orders with stable preset/row IDs, one-time initialization and atomic shop-list saves that preserve existing lists on failure.
- Added Fill order over multiple runs: initial ownership is credited once, verified purchases survive consumption, movement, cancellation and reload, and affordable partial passes remain pending. Standalone orders are isolated by character/preset and offer Start new order after completion; DAD orders carry absolute progress on their own association.
- Kept refill/spending behavior and step examples. Finite-order callers must support progress; partial results wait for cleanup. Versions and client files are unchanged.

## Unreleased

- Exclude beast companions (`Pet`) and chocobos (`Buddy`) alongside NPC party members before object rules, clearing remembered state so they cannot become targets, follow anchors, or ghosts.

- Build only the ADS plugin in GitHub Actions so regression-test compilation does not block release artifacts.

- Keep looking for a repair NPC near the inn while NPCs load, up to the existing 75-second stage or 120-second overall timeout. Preserve the 80-yalm search radius, cancellation, and repair continuation; live verification remains pending.

- Manual Update (including `/ads mini`) now reloads all four data categories even when downloads are unchanged, then runs Stop followed by Start Inside once if ADS still owns the same duty run. Zoning and Yes/No dialogs defer the reloads; failed downloads/reloads, Stop, Leave, logout, duty changes, or a new ownership run prevent the pending restart. The selected custom object-rule preset and its overrides are preserved; automatic updates keep their existing reload behavior.

- Fixed Otopa Pottopa inn entry in Ul'dah to stop at the existing accessible counter approach point within one yalm, including while waiting for the menu. Inn entry no longer replaces an active vnavmesh path or pending pathfinding request on its movement retry.
- Added manual inn destinations to /ads npcrepair yesinn and ADS.StartRepair: uldah, gridania, limsa, ishgard, crystarium, sharlayan, and tuliyollal. Removed the automatic starting-city test circuit and its saved checkbox. Trips retain a separate starting room and required destination room, reach the mender even with fully repaired gear, and finish only after confirmed destination entry. Stop, logout, unload, failure, and timeout cancel the trip and pending navigation. Startup logs identify I403-manual-inn-destinations-03.
- Fixed inn repair starting its walking route before the final aethernet hop settled. Repair and inn-entry movement now wait for the navigation mesh, cancel pending paths during loading, and retry the current destination after three seconds without movement, even when vnavmesh still reports an active path. Existing attempt timeouts still apply.
- Explicit inn destinations now teleport directly when starting in another inn; the door exit is used only when already in the requested inn. Plain yesinn retains its automatic behavior. Inn routes resolve their unlocked main aetheryte through the aethernet group, allowing Limsa's Upper Decks inn route to use the Lower Decks crystal. The preceding build completed live Ul'dah and Gridania destination runs and plain yesinn runs from inside an inn, outside in Gridania, and another territory. These follow-up changes and Limsa still require live verification.

- Added `/ads npcrepair yesinn` and `ADS.StartRepair("npc-yes-inn")`: exit an inn through its door, repair nearby, then return to the same room; outside starts use the current city or cheapest unlocked inn route. Fully repaired gear skips repair interaction, and utility status remains active until room entry. Missing doors, menders, and failed entry report failure without an exit-to-teleport fallback. Inn and innkeeper detection now uses game IDs across client languages.

- Shop Lists now waits for the existing bounded cleanup to close returning NPC menus after refill and spending batches that held a shop, preserving finite-order cleanup. Preview quantity fields now read "If owned <" and "Refill to >=" on separate lines; inline and guide examples explain that setting both to 130 targets 130 owned items (10 owned means buying 120 with single-item bundles), while `/ads shop <itemID> 130` requests 130 additional items.

- `/ads loot` now processes eligible loot in Need, Greed, and Pass modes independently of ADS's main automation switch. Off still stops loot actions; saved loot modes, login/zoning/cutscene safeguards, and existing roll-selection rules are preserved.

- Shopping now approaches Auriana in Mor Dhona at the specified stand-off point within one yalm before interaction, even when her live position is available. Tomestone confirmations match the separate item preview and exact submitted total cost, including four Unidentifiable Bones for 600 Poetics, while retaining single-use confirmation and exact inventory/currency verification.

- Compact Controls now includes normal-use Enable/Disable buttons for QSTcomp and HealBot, plus Reset RSR Healing using RSR Off and Coppelia's eleven healing defaults independently of HealBot. Unavailable plugins and failed requests use ADS chat status.
- Object Rules now shares named custom presets as versioned sparse transfers through clipboard or files, including saved and draft-changed complete contexts, empty overrides, and custom-only contexts while omitting inherited duties. Import previews validate the full payload and show the destination; Import and Save creates or merges included contexts, preserves unrelated files, and activates the preset with clean-draft, empty-override, and conflict safeguards. Legacy imports and explicitly labeled full-manifest exports remain available.
- Object Rules now separates preset management, sharing, and draft editing with clear action labels, including New Preset, Delete Preset, Load DEFAULT Cache, Import Preset, Export Overrides, File Import/Export, and Selected Duties/Selected Rows/Filtered Rows exports. Compact-mode behavior is preserved.

- An explicit `/ads leave` now immediately accepts a recognized localized return-to-entrance prompt when the current duty is confirmed complete and the player is dead, then resumes duty exit after respawn and zoning. Completion is scoped to the current duty and cleared on new entry, exit, or logout; completion alone, unknown completion, and unrelated dialogs never authorize this confirmation.

- Shop Lists now bundles ten ARR Zodiac and HW Anima step examples through Add example. Each creates an editable, inventory-only Poetics refill preset without starting purchases or replacing existing presets. Umbrite is an adjustable 20-item batch; Mysterious Map and its farming remain with Loot Goblin.

- Tomestone shop validation now reads the current currency addon's displayed prices and callback indices, cross-checks item/bundle data against AgentShop, and requires a unique currency icon match in current item data. This fixes Hismena's empty AgentShop cost slots without bypassing live price validation.
- Shopping cleanup sends the shop cancel callback so the NPC event can finish. The relic test closes any returning parent shop menu within its existing bounded cleanup phase before dispatching the next item.
- Shop cleanup now waits for pending vnavmesh pathfinding as well as active movement before reporting a stopped path. Failed exchange-row validation reports bounded live item and currency values, and navigation timeouts report the remaining vendor distance.
- Shop vendors behind counters now use ADS's existing hitbox-adjusted interaction reach. An already reachable vendor does not start a new path, and arrival within that reach stops the owned path before interaction.
- Relic-test reload cleanup explicitly invokes the existing shopping navigation stop, including a path left behind by an earlier finished runner.

- Added the opt-in relic purchase debug test under Main > Tools (Show debug sections). It binds to the character that arms it, attempts one additional item per material using only Poetics, and saves verified progress across reloads. Stop retains the reload selection; disabling it cancels pending and active work. Interrupted purchases require exact item and currency reconciliation before retrying, and completed tests remain idle until explicitly reset.
- The relic test covers the 13 direct-shop ARR Zodiac and Heavensward Anima materials. Mysterious Map and its farming belong to Loot Goblin and are excluded from the test. Completion checks the required item IDs, preserving the existing task selection, character binding, and all saved successes without a reset or migration. Unique-item ownership, currency, capacity, unlock, and runtime shop checks remain enforced. Existing public shopping IPC behavior is preserved.
- Live verification completed the 13 normal-shop materials, each with an exact +1 item and matching Poetics decrease; saved progress survived reload and the finished pass left no interrupted purchase. Mysterious Map remains unpurchased.

- Fixed Magitek Armor interactions using hitbox-adjusted reach for the existing `2.0y` initial and `1.5y` close-recovery attempt gates. Rule matching, navigation, diagnostics, settling, cooldowns, attempt limits, and mounted-state completion are unchanged.
- Corrected the Praetorium Research Level `XYZForceMarch` destination in BotologyUpdates to Nero's arena centre at `-164,-103.8,0`, preserving its Y coordinate, priority, completion behavior, and separate ordinary waypoint.

- `/ads obj` now supports native header sorting and Shift-click priorities across all eight data columns, with nearest-first distance by default, live reordering, missing values last, and compact two-line header help. Row actions and RULE popups follow object IDs, and DhogNav availability preserves table settings.

- `/ads obj` now fits columns to their contents, supports dragging dividers and double-clicking to refit, and scrolls horizontally with Name and the header frozen. Width adjustments last for the session without saving column settings.

- `/ads obj` now shows Current map ID, including in Compact view. Loading, logout, and unavailable map state show Unavailable. Existing duty updates refresh the live map without changing automation rules.

- Add `ADS.OpenPlayerObjectExplorer() -> bool` through the existing IPC service to open the object explorer with all filters cleared and Player selected.
- Show a DhogNav whitelist action only on player rows while `DhogNav.AddWhitelistedPlayer` is available. Resolve each clicked player by object ID, pass the complete name and home server, and report unavailable-plugin or save/edit failures in the existing action status, including Compact view. DhogNav's parasite checkbox does not control availability.

## 2026-09-08

- Corrected the legacy map-effect hook to use the generated SetDirectorData delegate (void return and byte buffer), preserving one original call and existing event tracing.

## 2026-09-07

- ADS now remembers Debug Mode set through `/ads debug on|off` across reloads and restarts, defaulting to off. Existing debug permissions and input-release behavior are preserved; held strafe input is never restored.
- NPC repair now skips repeat movement commands while vnavmesh is pathfinding or following a path, checking every two seconds. Initial destinations and new inn waypoints remain immediate, and unavailable IPC retains the existing retry behavior.

## 2026-09-03

- Stopped every inline Object Rules editor—including names, layer/coordinates, numeric fields, waits, notes, and debug commands—from rebuilding filtered rows and context descriptors for every typed character or held adjustment. Edits still update the in-memory draft and `UNSAVED` state immediately; the expensive editor refresh is coalesced until the active edit ends, and JSON writes remain explicit through Save.
- Fixed close-recovery interaction retries getting trapped in repeated tiny vnavmesh moves when its `0.8y` stand-off destination stopped the player just outside the matching `0.8y` attempt gate. The destination remains `0.8y`, while a bounded `1.5y` arrival gate now turns that close move into the next interaction attempt.
- Made Object Rules lead with a Compact-visible current-area source/row/save-state banner, added one full-width reset for every filter and selection without discarding draft edits, bounded the filter/context controls, and enlarged promotion actions. Custom rules can now store a debug-only one-shot command that runs only for safe owned activations from saved custom overrides while `/ads debug on` is active; PR-ready promotion strips those commands, preserves the personal preset, and reports the stripped count in toast and chat.
- Kept initial direct interactions at `2.0y`, but retry-capable required objects, treasure doors, expendables, and treasure-follower sacks now resume navigation to the preferred `0.8y` stand-off after a rejected or unconfirmed attempt. These close-recovery retries retain the pending target and suppress the `2.5y` close-XZ fallback while preserving existing cooldowns, attempt limits, and one-shot behavior.

## 2026-09-02

- Removed the Object Rules per-frame filter/sort, full-manifest alignment, missing-field allocation, and hidden-column control costs; filtered row indexes are change-driven, while full width scans are limited to loads, structural changes, and explicit auto-fit. Every Object Rules action button now has hover help, including disabled prerequisites, and **All contexts** promotion now copies every saved custom override, including empty and custom-only shards, without being narrowed by row filters.
- Replaced the object-rule monolith with indexed Global/territory shards. Custom presets are now persisted executable sparse overlays: missing contexts inherit `DEFAULT`, complete and empty overrides replace one context, custom-only territories work, and invalid active selections fall back safely to `DEFAULT`.
- Protected ordinary `DEFAULT` editing behind session-only `/ads debug on`, added context-aware shard conflict checks, and rebuilt Object Rules around a responsive compact-capable layout, content-fitted resizable columns, cached file-state labels, and a remembered searchable multi-context checklist.
- Added batch revert and clean-saved batch promotion for checked custom overrides, including eligible/skipped summaries, empty/custom-only warnings, one-time index updates, local-change confirmation, permanently visible checkout configuration, and a direct Rules walkthrough. Promotion never stages or otherwise mutates Git state.
- Repaired BotologyUpdates checkout activation: Settings and Object Rules now share one live checkout state, **Use checkout** accepts either the repository root or `ads/territories` (including pasted quoted paths), saves the canonical Git root immediately, and keeps invalid input visible without replacing the last valid checkout. Current-directory detection was removed, and open/promotion actions use the saved root.
- Switched remote object-rule updates to complete validated indexed shard downloads, made fresh invalid/incomplete shard caches refresh immediately, included obsolete-shard removals in completion summaries, and removed current-build MATURE proposal synchronization. The two BotologyUpdates object-rule monoliths remain frozen for legacy builds.
- Restored the direct interaction attempt range from 3.0y to 2.0y after Cutter's Cry exposed rejected out-of-range interactions; retry identity matching remains 2.0y.

## 2026-09-01

- Restored Object Rules so `DEFAULT` is the sole live runtime ruleset; ordinary presets remain parked editing copies, while missing or invalid `DEFAULT` reloads retain the last valid runtime rules.
- Clarified `CombatFriendly` help: a normal settled interaction suppresses that position until duty reset unless the progression object is explicitly repeatable.

## 2026-08-31

- Object Rules now reloads valid `DEFAULT` file changes without discarding the last valid rules on failure. `MATURE-PROPOSALS`, Dialog Rule presets, and BotologyUpdates file ownership are unchanged. The Main state strip now also shows the current planner object name, or `None` when there is no target.
- Frontier discovery now includes the game-authored `MapMarker` area-boundary indicators that connect maps inside the current duty territory. ADS seeks the marker as live map truth without treating proximity as completion; the existing `BetweenAreas`/`BetweenAreas51` unsafe-transition hold clears movement during the crossing, and the settled live `MapId` supplies the next subarea. No object-rule kind, authored transition coordinate, packet reader, or transition completion state was added.
- Quick Controls now shows **Disable QSTcomp** only while ADS debug mode is enabled through `/ads debug on`.

## 2026-08-30

- Object Rules now names the exact authored rule driving the current planner objective and highlights its clean saved `DEFAULT` row, with a direct action that clears hiding filters and scrolls to it.
- Rebuilt Main > Duties as a compact current-duty coverage surface and replaced the former maturity/readiness editor with Duty Manager: numeric M0-M3 display, expansion/family/rule/waypoint/Dawntrail/current/changed/selected/MSQ/planned/override/note filters, clipped rows, selected-duty editing, and diagnostic rule deep-links. Coverage is now one O(duties + rules) snapshot per draw and reports associated, enabled, valid-waypoint, redundant-scope, global, and unresolved counts while runtime scope matching remains conjunctive.
- Expanded Object Rules with clipped rows, final-column bulk selection, exact affected-scope deletion confirmation, one-step deletion/partial-import undo, complete-duty/delta/current-filter import modes, protected globals, matching partial exports, and dirty-draft-safe external refresh conflicts. `MATURE-PROPOSALS` is an editable non-default preset with a last-write-based 24-hour refresh clock, confirmed Refresh Now, and a clean saved JSON contribution helper.
- Remote JSON completion now reports the exact changed files so ADS reloads only affected data. The shared proposal candidate remains outside `DEFAULT`, and no IPC or JSON schema version changed.

## 2026-08-29

- Expanded Shop Lists with stable preset/row IDs, targeted refill and spend-until-currency/capacity modes, exact per-preset currency triggers, per-row repeatable and inventory/retainer ownership rules, vendor-bundle refill ceilings, Base64 sharing, deterministic vendor/NPC/territory/XYZ catalog search, purchase-free Test previews, and correlation-bound JSON IPC. Non-repeatable completion is reported per operation for Dad associations; ADS never deletes Dad plans or schedules. Existing `/ads shop` and `ADS.StartShopPurchase` remain exact-additional single purchases.

## 2026-08-28

- Added the user-configured `HyperFixatedAttackTarget` rule for live, targetable BattleNpcs in validated solo duties. It preempts normal ADS objectives, keeps exact object identity across planning/execution, acquires and heartbeats FrenRider's five-second hyper-focus lease before targeting or movement, approaches to 2.0y, chains directly to another valid hyper target, and stops/reports if the lease is unavailable. ADS still sends no rotation commands.
- Recovery navigation now retires only the selected stuck ghost and replans when player movement stays under 0.5y for 12 seconds, instead of resending the same unreachable destination indefinitely.

## 2026-08-25

- ADS now checks the BotologyUpdates JSON cache hourly while loaded and starts the existing refresh only when a cache file is missing or older than 24 hours.

## 2026-08-20

- Fixed the v0.6.2.1 release build by correcting URL-safe Base64 padding handling for shop-list imports.
- Added a startup warning when LazyLoot is loaded, with actions to open `/ads loot` or permanently dismiss the message. Existing loot automation and ADS's owned-duty LazyLoot disable behavior are unchanged.
- Added persisted Shop Lists, opened with `/ads shopper`, compact Controls, or Main > Tools. TeamCraft, Crafting as a Service, and Artisan clipboard imports resolve locally; live inventory and current-character XA Database retainer holdings reduce desired totals; previewed purchases run sequentially through the existing fail-closed shop runner with same-shop reuse and immediate stop/cancel behavior. Existing `/ads shop` and IPC contracts are unchanged.
- Map openers now include Leather Sacks in normal treasure-coffer planning, allowing the existing sack priority to beat Arcane Sphere progression instead of repeatedly targeting the sphere. Treasure-follower loot handling and the existing coffer distance, vertical, and rule gates are unchanged.

## 2026-08-17

- Higher/Lower cash-out recovery now makes one `OpenChest` callback attempt against the hidden `TreasureHighLow` addon after the existing 25-second missing-surface wait, then remains blocked if the callback cannot be sent or verified. It never substitutes a High/Low direction and does not resend the recovery attempt.
- Offline shop routes now preserve a reached, floor-resolved vendor stand-off long enough to use the existing bounded NPC interaction, instead of retargeting an unreachable NPC center. Currency parsing, menu routing, and interaction failure fallback are unchanged.
- Moved Object Explorer Search onto its own full-width row while preserving the existing filters, Clear behavior, and tooltips.

## 2026-08-13

- Exposed the existing shop-reuse switch over IPC as `ADS.SetShopKeepOpen(bool) -> bool`, so an IPC caller buying several items from one vendor can hold the shop open instead of paying a close-and-re-interact cycle per item. Nothing changes unless a caller opts in; `KeepShopOpen` itself, purchase behaviour, and every other endpoint are untouched. Repeated restock testing showed that closing and re-interacting can leave the character in a stale NPC event, causing later interactions to be ignored.
- Fixed the documented way to end such a chain. `KeepShopOpen` told callers to close the held shop with `CancelUtility`, which cannot work: both `Plugin.CancelUtility` and `UtilityAutomationService.Cancel` return immediately unless a purchase is still running, and a held shop exists only once the purchase is terminal. Turning `SetShopKeepOpen` off now closes it (new `ReleaseHeldShopUi`, which never touches a live run's UI), and the remarks no longer point at cancel.

## 2026-08-09

- Fixed treasure-follower BMRAI/VBM targets inside ADS by resolving the live opener through party Content ID, then slot, then exact name or name-plus-world identity, and removing only an authoritative concatenated world suffix that leaves a two-part character name. Both providers, accepted-command state, reapply matching, keys, and diagnostics now share that resolved name; ordinary names, world-like surnames, missing party/world data, regular-duty Slot1 resets, and treasure-exit cleanup remain unchanged.
- Added a default-off XA Database glamour-loot option to Loot Controls. It queries current-character ownership only for equippable loot, requests Need for missing gear, retains live Greed/Pass caps, and preserves the configured base mode with rate-limited diagnostics when IPC data is unavailable or incomplete.
- Added `/ads l` as an exact toggle alias for `/ads loot`.
- Expanded Object Explorer with guarded Foray level/element columns and tooltips, trimmed pipe-separated OR search, and a session-only compact view that keeps actions and filters visible while hiding contextual/status text.
- Debug x64 solution compilation succeeds with zero errors; the two warnings are obsolete-distance references in unchanged `ExplorerSnapshotExportService.cs`. No tests or live-client checks were run.

## 2026-08-02

- Parked ADS in the current Foray, Diadem, and Ocean Fishing territory list (512, 514, 515, 624, 625, 656, 732, 763, 795, 827, 900, 901, 920, 929, 939, 975, 1163, 1252, and 1346). On entry ADS now uses its normal stop/cancel cleanup, stays Idle with an inactive status, skips alliance lookup and all further framework work, and rejects duty, inn, repair, desynth, materia, and shop starts before their side effects. UI, Stop Ownership, and Cancel Utility remain available; normal duties and Eureka Orthos are unaffected.

- Made framework hitch profiling opt-in through Advanced Settings. It now defaults off and adds no per-section `Stopwatch` or delegate allocation during normal framework updates; enable it temporarily to retain the existing 100 ms hitch report, slowest-section diagnostics, and five-second log cooldown.

- Fixed shop purchases terminating as `ui-mismatch` whenever the game pluralises the item name in its confirmation prompt. `ShopConfirmationToken.TryConsumePrompt` matched the sheet item name as an exact whole word, so `Purchase 2 ragworms for 16 gil?` never matched token item `Ragworm` and ADS declined to dispatch Yes to its own confirmation; items whose plural equals their singular, such as `Krill`, masked the bug. Item-name matching now also accepts the `-s`, `-es`, and `-y`/`-ies` plural forms. Quantity and every currency amount are still matched exactly and the ten-second boundary, one-shot consumption, and fail-closed rejection are unchanged, so a prompt that does not describe the exact transaction is still refused.

- Added opt-in reuse of an already-open shop across consecutive purchases (`ShopPurchaseRunner.KeepShopOpen`, surfaced as `UtilityAutomationService.ShopKeepOpen` and `Plugin.SetShopKeepOpen`). It defaults to off, so existing single-purchase behaviour is unchanged. While enabled a successful purchase leaves its shop open, and the next `Start` whose resolved offer matches the visible shop kind enters `validating-ui` directly instead of navigating, interacting and reopening; `ValidateShopUi` still checks the live shop id and row against sheet data, so a different shop is rejected rather than bought from, and a non-matching visible shop fails the start closed. Failed and cancelled runs still close the UI, and the caller owns closing the shop once its batch ends.
- Measured on a gil vendor the character is already standing at, the first purchase is unchanged at ~1.4s while each subsequent purchase from the same shop costs ~0.27-0.33s instead of ~1.4s, because the shop addon open (0.7-1.4s of that) is paid once rather than per item; buying two each of three baits completed in 1.9-2.8s over four consecutive runs.
- Release suite passes 408/408 including the new confirmation-token plural test, and the Release plugin build succeeds with zero errors. Live verification covered gil-shop purchases only.

## 2026-08-01

- Refined Object Explorer into a two-row filter toolbar. `Filter by Lv.` now explicitly enables the session-only character-only Exact/`<=`/`>=` level filter; its first enable uses the local character level, values are positive integers with no maximum, and **Clear filters** disables and resets it while preserving the existing additive filters and row actions.
- Added bounded ADS-owned shop-confirmation diagnostics: each successful purchase callback logs its armed shop/token context and total costs; an unreadable visible `SelectYesno` logs one warning per token; readable mismatches log the expected token and displayed prompt; and Yes dispatch logs success or failure. Exact validation, the ten-second timeout, callback behavior, and fail-closed rejection remain unchanged.
- Focused Debug x64 confirmation-token tests pass 9/9, including exact 99-quantity prompt acceptance and mismatch rejection; the full Debug x64 suite passes 407/407 and the Debug x64 plugin build succeeds with zero warnings and errors. No live purchase or confirmation was run.

## 2026-07-31

- Added an ADS-owned, current-run XA Slave dialog/cutscene skipper fallback. TextAdvance remains authoritative when enabled; `/ads skipper [on|off]` never changes TextAdvance or FrenRider, does not persist configuration, and cleans XA Slave state only after ownership/leaving ends.
- Expanded optional alliance scoping from A/B/C to A-G. Blank scope remains wildcard, while invalid or currently unresolved explicit scope continues to fail closed; schema version remains 1.
- Stopped duty-object rule loads from automatically rewriting saved rule fields through built-in migrations; the rule editor now reloads the manifest exactly as authored.

## 2026-07-30

- Added optional A/B/C alliance scoping to object rules. ADS resolves the current alliance from the typed `_PartyList` UI only while `IPartyList.IsAlliance` is true; blank scope remains wildcard, while invalid or currently unresolved explicit scope fails closed across object rules, manual destinations, cardinal holds, previews, and editor filters. Schema version remains 1.
- ADS outside/inside starts now best-effort-send `/xldisableplugin AutoDuty` before ownership work for shared chat, UI, IPC, and operator-API behavior. Dispatch failure is logged without blocking the requested start; Resume is unchanged. Credit <@196286096726949888>.

## 2026-07-25

- Made `Treasure coffers: OFF` a hard observation-layer ignore for treasure objects and recognized coffer, chest, and treasure-dungeon sack names. The policy now wins before authored object rules and clears remembered loot/suppression state so normal ADS and Fren followers cannot select a live or ghost coffer, while enabled behavior and non-loot progression such as treasure doors remain unchanged.

## 2026-07-23

- Fixed nested shop menus by resolving each unique live handler through `EventHandlerSelector.Option.GlobalIndex`; `LocalIndex` and sheet indexes are now diagnostic only, including the handler `3276827` local-index-3/global-callback-2 regression and its nested callback-4 path.
- Unified owned confirmation and exact-delta verification under one ten-second timeout. Exact readable prompts are accepted once through the boundary, expired or mismatched prompts fail closed, and unreadable prompts time out without resending the purchase callback or changing candidates.
- Replaced fire-and-forget shop navigation cleanup with `vnavmesh.Path.Stop` plus `Path.IsRunning` verification. Interaction, menu selection, retargeting, and identical-cost fallback now wait in a bounded `stopping-navigation` phase; persistent or unverifiable movement terminates as `no-route` before any NPC or purchase callback.

## 2026-07-22

- Expanded deterministic shop purchasing across GilShop, direct and FATE-routed SpecialShop, InclusionShop, Grand Company, and Free Company families; added recursive carrier discovery, live handler-index resolution, deferred gates/balances, audited mixed currencies, exact coproduct verification, family-specific live validation, and owned one-shot confirmations.
- Fixed one-shot shop travel by accepting the owned teleport gil fee after confirmed arrival and waiting for callback-owned confirmation prompts to become readable without weakening exact mismatch or delta checks.
- Added ADS-owned duty camera recovery for idle-camera and all first-person control modes with one shared ten-second cooldown and one-tick ADS-owned key release.
- Added a once-per-entry solo-duty toast explaining `/ads leave` recovery.
- Added five independent, replayable Guided Setup flows plus version-21 one-time-new-install hub behavior and migration-safe optional completion flags.
- Added a default-enabled Settings > Automation toggle for BMRAI/VBM regular-duty follow resets. Disabling it skips the `/bmrai follow Slot1` and `/vbmai follow Slot1` commands on the next regular-duty entry while preserving pending treasure-follow shutdown, treasure opener follow, ordinary cleanup, and treasure-exit cleanup behavior.

## 2026-07-16

- Added a deterministic offline vendor-placement generator and checked-in catalog/audit sourced from local xivdatamine sheets, an offline Garland NPC browse snapshot, and the established ItemVendorLocation location corrections.
- Shop placement resolution now prefers live LGB data, then `Level`, then the embedded offline fallback; explicitly marked corrections can replace known-invalid primary placements while ordinary catalog rows remain fallback-only.
- Added vnavmesh floor resolution for catalog X/Z coordinates after territory entry, live-NPC destination retargeting, and callback-free identical-cost fallback when floor lookup or candidate validation fails.
- Corrected the Versatile Lure regression: the teleport-accessible Limsa merchant is supplied by the offline correction catalog, not `planevent.lgb`.

## 2026-06-14

- Fixed BattleNpc rule shadowing by applying distance/Y gates before effective-rule selection. Failed actionable BattleNpc rules no longer block manual/frontier movement, lower eligible matching rules can win, planner/frontier share one eligibility policy, and Analysis JSON exposes raw/eligible/gate-suppressed monster diagnostics.
- Added authoritative `ADS.IsDutyOwned()` IPC for cross-plugin movement ownership. It reports owned/leaving modes only while live instanced-duty truth is active.
- Rebuilt the object-rules guide around the Object Explorer `RULE` workflow, goal-based class selection, rule-resolution order, common examples, and advanced JSON reference.
- Added class-specific row help, relevant-field tooltips, required/recommended/optional/ignored metadata, red/amber/dim table cues, and missing-required-field validation without clearing ignored stored values.

## 2026-06-09

- Rebuilt Main around persistent operator controls and Overview, Duties, Tools, and Diagnostics tabs while preserving existing actions, disabled-state rules, catalog behavior, live truth, specialist launchers, updates, and JSON copies.
- Replaced the dense Duties table with a searchable responsive catalog/detail dashboard, grouped family filters, and compact collapsible rule coverage; renamed the user-facing `FourPlayerSyncCleared` label to `[Synced Party Cleared]` without changing its enum or JSON value.
- Reorganized Settings into General, Automation, Data & Rules, Advanced, and About tabs without changing configuration fields or data actions.
- Rebuilt compact Controls with full action labels, shared inside/leave disabled rules, grouped shortcuts, concise live status, and unchanged debug-strafe release behavior.
- Replaced the legacy tester-heavy README/GUIDE split with a product overview, operator manual, exhaustive command reference, rule-authoring guide, and troubleshooting/evidence guide.

## 2026-05-18

- Matched treasure-follower door movement to map-opener door follow-through. Followers still never click TreasureDoor/coffer objectives, but they now use the shared through-point, stale-floor detection, and door-frame jiggle recovery before cycling failed same-room door candidates.
- Added a separate, default-off Higher/Lower VFX datamining gate. Normal diagnostics and solver traces stay unchanged; bulky `HigherLowerDiagnostics\Datamine` sessions write only when the experimental datamine checkbox is enabled.

## 2026-05-17

- Fixed treasure-follower routing after the initial manual XYZ. Followers now prefer live TreasureDoor route targets mapped onto static room metadata, skip stale entry/start points once entry progress is proven, and expose the current route key/source and live-door count in status JSON.

## 2026-05-10

- Replaced packaged JSON rule sync with a botologyupdates-backed config cache. ADS now refreshes `duty-object-rules.json`, `dialog-yesno-rules.json`, and `duty-maturity.json` from raw GitHub when cache files are missing, when ownership starts with a cache older than 24h, or when the operator clicks `Update`.
- Added `duty-maturity.json` as the data source for duty clearance/support metadata. The Lumina duty catalog still owns identity/category/level, while maturity rows overlay `ClearanceStatus`, `SupportLevel`, planned-test flag, and support notes.
- Added dialog-rule presets matching object-rule presets: live `DEFAULT`, parked `dialog-rule-presets/*.json`, full-manifest clipboard/disk import/export, create/delete, and `@` reset from the current DEFAULT cache.
- Removed packaged `duty-object-rules.json` / `dialog-yesno-rules.json` from plugin output; built-in JSON is now only a minimal first-run fallback.
- Loosened normal manual `XYZ` arrival to a `2.5y` 3D radius while keeping force-march destinations on the tighter `1y` rule. Manual destinations now ghost as `ManualDestinationNoProgress` if player movement stays under `0.5y` for `12s`, and status/analysis JSON expose the active manual target, distance, progress age, and last manual ghost reason.
- Cleaned Copperbell rule migration/source data: `Sealed Blasting Door` stays `Expendable`, stale `BattleNpc` `Lift Lever` rows are disabled, malformed manual `XYZ` rows with `ObjectWorldCoordinates` are disabled, and lever use stays on the generic non-kind row plus positional ignores.

## 2026-05-09

- Added `Process dialog rules outside owned duties`, enabled by default. Dialog rules now run while ADS is enabled, the character is logged in, and the game is not zoning, including idle, observing, unsupported-duty, and outside-duty states; disabling the setting restores the older owned-or-leaving instanced-duty gate.
- Added `processDialogRulesOutsideOwnedDuty` to Status JSON for tester confirmation.
- Tightened in-combat `BossFight` routing. ADS can still approach a live boss while combat is active, but only until it reaches `5y`; then it targets the boss, stops navigation, and combat-holds that same live boss until combat clears instead of chasing it again.

## 2026-04-12

- Restored the missing GitHub Actions release surface for ADS by adding the standard `build-release.yml` workflow with ADS-specific solution, manifest, and packaged `latest.zip` paths.
- Expanded `GUIDE.md` with a beginner-first duty-maturity testing walkthrough, command list, window tour, rule-authoring quick start, and evidence checklist for helpers who are scouting or validating ADS rules.
- Fixed Brayflox-style manual staging reactivation after talk-NPC progression. ADS now stages a nearby authored `MapXzDestination` / `XYZ` against the live progression interactable, then ghosts that exact manual destination when the interact is actually consumed, instead of only ghosting it later if ADS walks back over the point or hits `BetweenAreas`.
- Reset ADS frontier/manual state and observation memory when leaving supported duty context, so repeated entries into the same duty do not inherit stale ghosts, visited manual waypoints, or remembered manual follow-through from a prior run.
- Added a planner safety seam that refuses stale-ghost backtracking while unvisited human-authored `MapXzDestination` / `XYZ` stages still remain unresolved.

## 2026-04-11

- Added `EventNpc` observation support to the interactable pipeline, so targetable talk NPCs can now be surfaced as live interactables instead of being silently skipped. Brayflox `Goblin Pathfinder` rules can now stay authored as `EventNpc + CombatFriendly`, and stale Brayflox migration no longer rewrites them back to `BattleNpc`.
- Added parked full-manifest rules-editor `PRESET`s alongside the live `DEFAULT` file, including full-manifest clipboard export/import, disk import/export for large manifests, non-deletable `DEFAULT`, and bundled-rule reset into the `DEFAULT` draft via `@`.
- Added `CREATE RULE` to Object Explorer so a new rules-editor row can be seeded directly from the current duty scope, live layer, object kind, base id, and exact object name.
- Added unsaved new-row highlighting/auto-scroll in the rules editor, plus a territory-aware `Layer` dropdown with a blank top option when live sub-area labels are known.
- Added main-window rule-atlas stats and explicit per-duty rule counts in the duty catalog so rule coverage volume is visible without opening the editor.
- Marked `Copperbell Mines` as `[1P Unsync Cleared]` after successful validation.
- Fixed layer-scoped BattleNpc truth leakage. If a visible BattleNpc only has authored layer-scoped rules and none of those layers match the current live sub-area, ADS now suppresses that mob from live monster truth instead of falling back to generic monster targeting. This covers Copperbell-style `Blasting Cap` / `Errant Soul` leakage from `B2` into `First Drop`.
- Fixed Copperbell-style planner dead states where a live BattleNpc was still visible in observations but a stale wildcard `Ignored` row with failing distance/Y gates caused planner to act like no monster existed. `Ignored` / `Follow` BattleNpc rows no longer suppress a live monster when their own gates fail, and the stale `Copper` row was narrowed to `EventObj`.
- Activated rule wait timing. `WaitAtDestinationSeconds` now holds after arrival and before the first direct interact send, and new `WaitAfterInteractSeconds` extends post-interact follow-through before ADS retries or moves on.
- Fixed monster-versus-progression arbitration so if both the live monster and the live progression interactable have active rules, ADS now spends that decision on rule priority first. Distance/Y only break ties or no-rule cases, which fixes Copperbell-style `Firesand` beating a better-priority `Blasting Cap`.
- Collapsed the rules-editor coordinate authoring surface back down to one `Coords` field plus one radius field. `a,b` now means map `X,Z`, `a,b,c` means world `X,Y,Z`, manual destination rows use that same single field, and the runtime storage remains backward-compatible underneath.
- Added positional matching for ordinary same-name rules. `ObjectMapCoordinates` / `ObjectWorldCoordinates` plus `ObjectMatchRadius` now let one row bind to one physical object instance without overloading manual `MapXzDestination` / `XYZ` fields.
- Pending progression interact follow-through now marks the interacted position used as soon as `BetweenAreas` starts. This restores the original Meridianum-style ghosting seam for one-shot objects that immediately transition or leave visibility after interact, such as `Disposal Chute`.
- Required progression-interactable suppression no longer clears on generic large relocations, and ADS now treats a non-disappearing required interactable as consumed when the interact displaces the player materially away from that same object. This restores durable ghosting for one-shot objects like Castrum Meridianum `Disposal Chute`.
- Manual `MapXzDestination` / `XYZ` staging can now beat tied live `Expendable` or `Optional` progression interactables, so Praetorium-style staging rows are not blocked by a same-priority generic `Shortcut`.
- Fixed stale Praetorium `Classification: XYZ` rows that were authored with 3D values in `MapCoordinates` instead of `WorldCoordinates`, and added a built-in migration so older live configs auto-promote those legacy 3-value payloads on load.
- Added a built-in migration for stale Praetorium `Castrum Defense` `Magitek Terminal` ignore rows that were authored as `EventNpc`. ADS now widens those legacy rows to wildcard-kind on load so the ignore still matches the live `EventObj`.
- Added precise manual `XYZ` destinations. New `Classification: XYZ` rows read `worldCoordinates` as authored world X/Y/Z, navigate directly to that point, expose separate XYZ counters in status/analysis JSON, and show up in the rules editor and ghost inspector.
- Fixed Praetorium-style deadlocks where visible progression interactables still blocked manual destinations even though they were outside their own distance/Y rule gates. Frontier/manual blocking now only respects live progression interactables that are actually eligible under the active rule gates, and the planner's idle explanation now says so.
- Added a Praetorium-only mounted combat branch. While `Mounted` stays true in territory `1044`, ADS now bypasses the generic `CombatHold`, reads the live mount-action list from the current mount row, prefers the best nearby cluster for mounted ground-target weapons, and fires the second mounted weapon while enemies stay in range.
- Marked `The Keeper of the Lake` as `[1P Unsync Cleared]`.
- Changed the rules editor's `Current Area + Global` filter to match duty/territory/CFC scope without applying live `Layer`, so same-territory Praetorium rows stay visible while authoring even if the current sub-area is different.
- If `Svc.Condition[Mounted]` becomes true during progression-interactable follow-through, ADS now treats that as a successful consume/use seam, marks the interactable position used, clears the old commitment, and waits for refreshed duty truth instead of retrying the mount object.
- Added `mounted` to the status and analysis JSON so Praetorium-style mount transitions can be validated directly from a capture.
- Fixed Praetorium-style layer swaps by preferring `ClientState.MapId` over the slower `GameMain` map source when both are available, so layer-scoped rules can see the new live sub-area sooner.
- Interactable follow-through is now revalidated against the current live `MapId` and ignore rules, so ADS drops stale committed/pending interactables after a layer swap instead of reusing them in the wrong sub-area.
- Split rule layer scoping into a first-class `Layer` field instead of overloading `DestinationType`, and added automatic migration for older live configs that still stored layer names in `DestinationType`.
- Fixed the rules-editor header tooltips so they wrap to a readable width instead of collapsing into 1-character vertical tooltips.
- Expanded the Ghost Inspector to show the current, remembered, and last-ghosted manual `MapXzDestination` state, making Keeper-style waypoint execution visible in the UI instead of only in the log.
- Widened the global `Automaton Queen` ignore row to an exact-name wildcard-kind ignore so player pet suppression is not coupled to one object-kind guess.

## 2026-04-10

- Added a standalone Ghost Inspector window plus `/ads ghosts`, and ghost recovery now respects the current live `MapId` so stale cross-layer ghosts stop hijacking Keeper recovery.
- Reworked the object rules editor with duty dropdown search + `GLOBAL`, auto-filled `Terr/CFC`, current-area-plus-global filtering, duty sorting, ObjectKind dropdowns, header tooltips, row base64 copy/paste, and tighter default column widths.
- Layer / `DestinationType` now scopes any rule to the current live sub-area, not just `MapXzDestination` rows.
- Manual `MapXzDestination` rows can now intentionally beat a worse live progression interactable when there are no live monsters or follow anchors and the waypoint row has the better priority, making Brayflox-style staging waypoints usable.
- Marked Sastasha as `[1P Unsync Cleared]` after successful validation.
- Changed the Keeper manual Map XZ rows from raw map id `201` to the human-readable active subarea name `Forecastle`, and clarified that `DestinationType` prefers live subarea names over numeric ids when available.
- Added a narrow BattleNpc direct-interact path for `CombatFriendly` rules, so talk targets such as Brayflox's `Goblin Pathfinder` can route/use through the interactable pipeline instead of staying stuck in `liveMonsters`.
- Marked The Stone Vigil as `[1P Unsync Cleared]` after successful validation.
- Added a close-range interact fallback for live interactables: if ADS is already near on X/Z but makes no X/Z progress for `3s`, it stops movement and starts direct-interact retries instead of looping forever in close-nav. This is aimed at vertical/barrier cases such as Sastasha corals.
- Repurposed `DestinationType` on `MapXzDestination` rows into an optional live-map layer selector. Leave it blank for any active submap, or set it to a map row id / map name to restrict that waypoint to one layer.
- Corrected the Stone Vigil boss rule to `Koshchei`, matching the live monster name so the `BossFight` priority path can actually trigger.
- Marked Halatali as `[1P Unsync Cleared]` after successful validation.
- Added BattleNpc-only `BossFight` rule classification plus planner/execution support. Live in-gate boss targets now beat nearby trash, treasure, ghosts, and remembered manual Map XZ follow-through, and they can keep routing through `InCombat` instead of falling into the generic `CombatHold`.
- Fixed the Sastasha coral rules to match the actual live `Blue/Red/Green Coral Formation` names, so those corals stop falling back to `Optional` and can beat the bad wall-path monster case.
- Allowed `CombatFriendly` interactables to bypass the generic `CombatHold` when the planner selects them during `Svc.Condition[InCombat]`, so duties like Keeper can still route to combat-safe progression targets while combat is active.
- Fixed interactable close-nav to use a full XYZ stand-off target instead of flattening to the player's current Y, and now keep interactables in navigation mode until they are actually close in 3D. This addresses vertical/barrier failures like Halatali `Chain Winch`.
- Tightened manual `MapXzDestination` follow-through so remembered manual points still survive transient live-monster visibility but now yield as soon as the planner promotes a live interactable, preventing stale wall-runs after progression targets like `Aetherial Flow` become live.
- Stopped `/vnav` immediately on any `Svc.Condition[BetweenAreas]` frame so sub-area handoffs cannot keep dragging a stale map-flag route after ADS ghosts the target.
- Stopped the frontier service from selecting fresh frontier / manual Map XZ targets during unsafe transition frames, preventing transition-time double ghosting and stale run carryover.
- Moved rule-backed interactable-ghost recovery behind live monster, live progression, live follow-anchor, and frontier / Map XZ choices so stale ghosts no longer steal control from stronger live truth.
- Kept selected manual `MapXzDestination` waypoints sticky during execution, so ADS no longer abandons them just because live monsters or interactables become visible before the configured X/Z arrival point.
- Added a bounded 3-attempt stationary follow-through for `Required` interactables, with immediate cancellation if `Svc.Condition[BetweenAreas]` starts.
- Reworked the duty-catalog readiness summary into four color-coded maturity cards for `[Not Cleared]`, `[1P Unsync Cleared]`, `[1P Duty Support]`, and `[Synced Party Cleared]`.
- Stopped the frontier service from pre-ghosting manual `MapXzDestination` points during the background sweep; they now only ghost on the execution-side 1y X/Z arrival check or on `Svc.Condition[BetweenAreas]`.
- Changed expendable interact follow-through so ADS keeps retrying the same live expendable from the same `<1y` `moveto` stand-off until the object actually disappears.
- Marked Castrum Meridianum as `[1P Unsync Cleared]` and promoted it into the active pilot set after successful validation.
- Added JSON-backed `MapXzDestination` / `MapXZ` manual waypoints. These parse `mapCoordinates` values like `11.3,10.4`, convert them to world X/Z on the current map, use the current player Y, prefer map-flag navigation with `/vnav moveflag`, fall back to direct `/vnav moveto`, and ghost the waypoint at 1y X/Z instead of waiting for an exact navigation finish.
- Froze the top header row in the duty-object and dialog-rule editors, and now ghost the current or last valid manual `MapXzDestination` waypoint as soon as `Svc.Condition[BetweenAreas]` confirms the area handoff.
- Frontier labels, map-flag placement, and manual `MapXzDestination` conversion now honor the live `MapId`, so ADS stops mixing labels from different sub-areas inside the same duty territory.
- Made treasure-coffer follow-through sticky once ADS commits to a coffer, preventing chest-vs-monster objective cycling while ADS is already routing to the selected chest.
- Added global JSON-backed `SelectYesno` dialog rules plus an ADS dialog-rules editor window, seeded with the imperial-identification-key barrier confirmation prompt.

## 2026-04-09

- Bootstrapped the `ADS` repository shell with a Dalamud project, manifest, repo wrapper, workflow, commands, DTR, Ko-fi link, and standard Dhog-style window controls.
- Added the first Lumina-backed 4-man dungeon catalog with pilot-wave support markers for Tam-Tara, Toto-Rak, Brayflox, and Stone Vigil.
- Added a passive duty-context observer, monster/interactable memory, planner explanation surface, ownership shell, and first IPC status/control providers.
- Added staged execution phases, vnav-backed monster/interactable movement, direct interact attempts, recovery ghost handling, object explorer diagnostics, map flags, and human-editable duty-object rules.
- Marked Tam-Tara as `[1P Unsync Cleared]`, added catalog clearance colors/stats, and added Castrum Meridianum plus The Praetorium to the planned test list.
- Added `DutyCompleted` handling so ADS drops owned execution and clears recovery memory when a duty ends.
- Added a territory frontier fallback from Lumina `Level` + `MapMarker` label points so no-object dungeon stretches can advance toward the next map label instead of backtracking through stale ghosts.
- Corrected the frontier label inspector to read labels from each map row's `MapMarkerRange` collection instead of assuming `MapMarker.RowId == Map.RowId`.
- Reused the verified `MapMarkerRange` labels as automation frontier waypoints when the older `Level`/`DataKey` join returns no points, using the player's current Y for label-derived navigation targets.
- Changed label-frontier movement to place an in-game map flag and send `/vnav moveflag`, with direct `/vnav moveto` kept as the fallback if map flag placement fails.
- Deferred active frontier target selection while live monsters or interactables exist, so labels like `Abacination Chamber` are only promoted during actual no-live-object gaps.
- Added live rule-file auto-reload, applied `Ignored` rules to `BattleNpc` observations, treated any matched in-gate human rule as an explicit priority override, and suppressed used progression interactables by duty/object/position until duty reset or large relocation.
- Added `Follow` duty-object rules for live-only NPC anchors such as Cid, deferred progression interactable suppression until after an interaction follow-through window, and made fallback map-label frontier selection heading-aware so Toto-Rak stops choosing behind-route labels like Ser Aucheforne's cell.
- Made BattleNpc objective selection priority-aware, so Required/Follow rules choose targets like The Black Eft before distance tie-breaks when their gates pass.
- Constrained `Follow` to BattleNpc rules only; non-BattleNpc Follow rows are migrated to `Ignored` and ignored at runtime so EventObj rules such as Field Generator cannot hijack follow-anchor planning.
- Marked Toto-Rak and Aurum Vale as `[1P Unsync Cleared]`, and promoted Aurum Vale into the active pilot set after successful validation.
