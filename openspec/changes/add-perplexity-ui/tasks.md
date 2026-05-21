# Tasks: add-perplexity-ui

## Review Workload Forecast

| Field                   | Value                                                                                                               |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Estimated changed lines | 700-1000                                                                                                            |
| 400-line budget risk    | High                                                                                                                |
| Chained PRs recommended | Yes                                                                                                                 |
| Suggested split         | PR 1 core engine focus plumbing → PR 2 Perplexity providers surface → PR 3 parallel configuration sections + polish |
| Delivery strategy       | ask-on-risk                                                                                                         |
| Chain strategy          | pending                                                                                                             |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

## Task 1 — Add focused-engine app state and shared engine access

Files:

- `src/TunnelAgent.Core/Models/AppSettings.cs`
- `src/TunnelAgent.Avalonia/App.axaml.cs`
- `src/TunnelAgent.Infrastructure/Engine/EngineRegistryService.cs`
- `src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`

Work:

- add persisted `ActiveEngineId` with CLIProxy default fallback
- switch app startup from single CLIProxy engine injection to shared `EngineRegistryService`
- refactor `MainWindowViewModel` to resolve focused engine from registry instead of concrete CLIProxy engine field
- preserve independent runtime state so focused-engine changes do not stop other running engine

Verification:

- app startup still loads with CLIProxy default focus
- changing focused engine persists and reloads correctly
- switching focus does not stop already running engine

## Task 2 — Add Perplexity account UI service and view models

Files:

- `src/TunnelAgent.Avalonia/Services/PerplexityAccountCatalogService.cs`
- `src/TunnelAgent.Avalonia/ViewModels/PerplexityAccountViewModel.cs`
- `src/TunnelAgent.Avalonia/ViewModels/EngineOptionViewModel.cs`
- `src/TunnelAgent.Infrastructure/Engine/Perplexity/AccountService.cs` (only if minimal API gap appears)

Work:

- add thin Avalonia-facing catalog service over Perplexity account persistence
- map backend settings entities into Perplexity-specific view models
- expose add/remove/set-default operations and change notifications
- keep token display masked after save

Verification:

- first account becomes default automatically
- changing default updates persisted state
- removing default promotes remaining account
- no raw token redisplay after save

## Task 3 — Refactor shared engine-derived UI state in main view model

Files:

- `src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`
- optional helper file if engine-scoped view state extraction becomes necessary

Work:

- replace direct `_engine`-bound derived properties with focused-engine-aware projections
- ensure shared header/status/start-stop/install/update/release-selection behavior follows focused engine
- isolate non-focused engine events so they do not overwrite visible state
- make reset-port behavior engine-specific instead of hard-coded `8317`

Verification:

- endpoint, status text, installed/latest version, and release selector change with focus
- start/stop and update actions target focused engine only
- both engines can remain running while UI focus changes

## Task 4 — Add Providers page engine selector and Perplexity management surface

Files:

- `src/TunnelAgent.Avalonia/Views/ProvidersView.axaml`
- `src/TunnelAgent.Avalonia/Views/ProvidersView.axaml.cs`
- `src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`

Work:

- add engine selector at top of Providers page
- keep existing CLIProxy provider catalog and available-models blocks behind CLIProxy focus
- add Perplexity-focused content block with:
  - engine summary
  - empty state guidance
  - account list
  - add/remove/default actions
  - masked token presentation
- add dialog flow for Perplexity account creation

Verification:

- CLIProxy focus preserves current provider UX
- Perplexity focus hides irrelevant provider controls
- Perplexity account CRUD works from UI
- focus switching only changes presentation, not engine runtime ownership

## Task 5 — Add parallel configuration sections for both engines

Files:

- `src/TunnelAgent.Avalonia/Views/ConfigurationView.axaml`
- `src/TunnelAgent.Avalonia/Views/ConfigurationView.axaml.cs`
- `src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`

Work:

- keep general settings shared
- add parallel CLIProxy and Perplexity engine configuration sections with similar structure
- make both sections visible for discoverability
- highlight focused engine section visually
- keep routing strategy only in CLIProxy section
- add Perplexity section content for version/install/status/port/account-session guidance
- ensure each section binds to correct engine-scoped state without cross-bleed

Verification:

- both sections visible in Configuration
- focused engine section visually emphasized
- CLIProxy section still shows routing strategy
- Perplexity section omits routing strategy
- each section shows correct version/status/port for its own engine

## Task 6 — Regression tests and manual verification checklist

Files:

- `tests/TunnelAgent.Tests/` new or updated test files for settings, view-model state, and Perplexity account catalog
- `openspec/changes/add-perplexity-ui/tasks.md` (checklist updates only if needed)

Work:

- add focused tests for engine focus persistence, engine-scoped state projection, and Perplexity account catalog behavior
- document manual verification for dual-engine runtime and parallel configuration sections
- verify existing CLIProxy provider behavior still works unchanged after Perplexity additions

Verification:

- targeted tests pass
- manual pass covers:
  - startup with CLIProxy default focus
  - switching focus with both engines running
  - start/stop each engine independently
  - Perplexity account add/remove/default
  - both configuration sections render correctly
  - routing strategy appears only in CLIProxy section

## Suggested PR chain

### PR 1 — Core engine focus plumbing

- Task 1
- Task 3
- minimal test coverage for settings + focused-engine projections

### PR 2 — Perplexity providers surface

- Task 2
- Task 4
- focused tests for Perplexity account catalog and Providers UX logic

### PR 3 — Parallel configuration sections + regression pass

- Task 5
- Task 6
- visual polish and manual verification notes
