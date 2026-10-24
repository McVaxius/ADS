# AI Duty Solver LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main: regular and compact](01-main-regular-compact-v3.png)
- [Settings, Mini and supporting surfaces](02-settings-mini-and-auxiliary-v3.png)

The second sheet depicts selected existing supporting surfaces. Other source windows retain their established layout and actions under the proposed shared appearance; this is not a claim that every scroll position or conditional state has a separate image. Browser companions retain browser controls. Main/Mini windows are distinct from density variants. Generated text and examples are design material, not runtime evidence.

## Source scope

Source commit: `1d5ad22a9c09fcccd628e1044560c371df6e1f67`. Existing dirty paths were inspected by count (6); none are included or changed by this mockup task.

## Source-verified control contract

- `ADS/Windows/MainWindow.cs:191` defines the exact tabs Overview, Duties, Tools, Diagnostics. Overview is the selected mockup surface.
- `ADS/Windows/MainWindow.cs:596` selects one primary action: Stop while owned, Start Inside while inside an unowned duty, otherwise Start Outside. Leave, Guided Setup and More actions remain beside it; Resume is in More actions and requires an instanced duty. The compact state strip and read-only Current Duty data are not editable planning controls.
- `ADS/Windows/ConfigWindow.cs:43` defines General, Automation, Data & Rules, Debug, Appearance, About. `:103` contains Startup checkboxes and the note that duty ownership is controlled by Start/Resume and Stop. `:169` is **Disable AutoDuty on ADS start**, not an enable checkbox.
- `ADS/Windows/QuickControlWindow.cs:128` contains five stacked actions: Start Outside, Start Inside, Resume, Leave, Stop. Start Inside, Resume and Leave are disabled outside a duty. `:157` defines the actual Tools shortcuts; this utility is not simply a scaled Main window.

The first generated supporting sheet is rejected: it invented startup, cloud route, plugin-manager and salvage controls. Regenerate from the revised literal prompts. The main palette-correction image also requires this control audit; a darker image alone is not source acceptance.

The selected supporting surfaces are General Settings, a small Automation/Appearance crop and ADS Controls. Desynthesis, DialogRuleEditor, DutyMaturityEditor, FrontierLabel, GearSalePreview, GhostList, HigherLower, InventoryUtility, LazyLootWarning, Loot, ObjectExplorer/ObjectRuleEditor, Reflection, RuleGuide, ServerEventExplorer, ShopLists, TreasureRouteEditor, VfxExplorer and Wizard retain their existing layouts, control types, explicit save/confirm actions and gates. They are not replaced by speculative vignettes. Browser/file-dialog companions keep their native controls.


## Existing UI source inventory

- `ADS/Windows/ConfigWindow.cs`
- `ADS/Windows/DesynthesisWindow.cs`
- `ADS/Windows/DialogRuleEditorWindow.cs`
- `ADS/Windows/DutyMaturityEditorWindow.cs`
- `ADS/Windows/FrontierLabelWindow.cs`
- `ADS/Windows/GearSalePreviewWindow.cs`
- `ADS/Windows/GhostListWindow.cs`
- `ADS/Windows/HigherLowerWindow.cs`
- `ADS/Windows/InventoryUtilityWindow.cs`
- `ADS/Windows/LazyLootWarningWindow.cs`
- `ADS/Windows/LootWindow.cs`
- `ADS/Windows/MainWindow.cs`
- `ADS/Windows/ObjectExplorerWindow.cs`
- `ADS/Windows/ObjectRuleEditorWindow.cs`
- `ADS/Windows/QuickControlWindow.cs`
- `ADS/Windows/ReflectionWindow.cs`
- `ADS/Windows/RuleGuideWindow.cs`
- `ADS/Windows/ServerEventExplorerWindow.cs`
- `ADS/Windows/ShopListsWindow.cs`
- `ADS/Windows/TreasureRouteEditorWindow.cs`
- `ADS/Windows/VfxExplorerWindow.cs`
- `ADS/Windows/WizardWindow.cs`

## Shared appearance contract

Auto and Picker retain their order and values; LCARS is the third option. Preserve native IDs, original actions, saved scope and semantic status colours. The proposed LCARS UI tones default off; the speaker and Settings preference are the same value, with no added volume or pack selector. Krangle obfuscates identifying text. Actual adoption waits for the FrenRider pilot.

Exact generation prompts are in [PROMPTS.md](PROMPTS.md).



