# Design: add-perplexity-ui

## Overview

Add engine-aware UI composition to Tunnel Agent so desktop app can switch focus between existing CLIProxyAPI workflow and new Perplexity workflow without duplicating shell chrome. Both managed engines may run in parallel. Keep one main window and shared status/config sections, but move focused engine state behind small app-facing adapters.

## Goals

- Reuse existing Avalonia shell, navigation, status, and configuration layout.
- Avoid forcing Perplexity behavior into CLIProxy provider catalog structures.
- Minimize backend changes by wrapping existing `IManagedEngine` and `Perplexity.AccountService` APIs.
- Keep implementation reviewable under one UI-focused change.

## Non-Goals

- Replace existing backend engine implementations.
- Add remote validation for Perplexity session tokens.
- Add generalized plugin architecture for arbitrary future engines.

## Architecture

### 1. Focused engine selector in app state

Introduce persisted focused-engine selection in app settings and expose it through `MainWindowViewModel`, with engine choice driven by Providers sidebar submenus rather than in-page buttons.

New state:

- `AppSettings.ActiveEngineId`
- default value: `EngineCatalog.CliProxyApi.Id`

This value represents UI focus, not exclusive runtime ownership.

`MainWindowViewModel` becomes engine-aware instead of bound to one concrete CLIProxy engine instance.

New constructor dependencies:

- `EngineRegistryService` for all managed engines
- existing `ProviderCatalogService`
- new `PerplexityAccountCatalogService` or equivalent thin UI-facing service wrapping `Perplexity.AccountService`

View model keeps:

- `IManagedEngine ActiveEngine`
- derived booleans such as `IsCliProxyEngineSelected`, `IsPerplexityEngineSelected`
- selectable engine options for Providers sidebar submenu entries

### 2. Separate engine-specific surfaces

Keep `Providers` section as primary management page, but branch its body:

- CLIProxy selected -> render existing provider catalog and available models UI
- Perplexity selected -> render dedicated Perplexity account management + Perplexity guidance

Do not overload `ProviderViewModel` with Perplexity session-token semantics. Perplexity gets its own small view models:

- `EngineOptionViewModel`
- `PerplexityAccountViewModel`
- optional `PerplexityAccountsViewModel` if grouping logic grows

### 3. Shared engine operations through focused-engine facade

Existing top-level commands already target `_engine`. Replace with active-engine resolution:

- start/stop
- check update
- list releases
- prepare version
- install/update
- port read/write
- endpoint text
- status text

Implementation pattern:

- `MainWindowViewModel` holds registry + selected engine id
- helper `GetActiveEngine()` or cached `ActiveEngine`
- refresh all engine-bound derived properties when selection changes
- subscribe to `StateChanged` on both engines, but only project active engine into UI properties

This keeps background update state on non-focused engine from mutating visible UI unexpectedly, while still allowing both engines to remain running.

## Components and File Changes

### App startup

`src/TunnelAgent.Avalonia/App.axaml.cs`

- replace direct `new CliProxy.EngineService(settings)` dependency with `new EngineRegistryService(settings)`
- construct Perplexity account service/catalog
- pass registry and services into `MainWindowViewModel`

### Main view model

`src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`

- replace concrete `_engine` field with registry + focused engine selection
- add persisted `ActiveEngineId` property
- add engine selector collection/bindings
- split provider-only and perplexity-only derived state
- add `ObservableCollection<PerplexityAccountViewModel>`
- add commands:
  - `SelectEngineCommand` or dedicated commands for each engine
  - `ShowAddPerplexityAccountDialogCommand`
  - `ConfirmAddPerplexityAccountAsync`
  - `RemovePerplexityAccountAsync`
  - `SetDefaultPerplexityAccountAsync`
- keep existing CLIProxy provider commands unchanged behind `IsCliProxyEngineSelected`

### New Perplexity UI service

Add thin Avalonia-facing service, likely under `src/TunnelAgent.Avalonia/Services/`:

- `PerplexityAccountCatalogService`

Responsibilities:

- load sorted Perplexity accounts from backend `AccountService`
- map settings entities into UI view models
- add/remove/set default
- raise change event for view model refresh

Why service layer:

- avoids putting persistence mapping logic in window view model
- mirrors existing `ProviderCatalogService` pattern

### New view models

Likely files:

- `src/TunnelAgent.Avalonia/ViewModels/EngineOptionViewModel.cs`
- `src/TunnelAgent.Avalonia/ViewModels/PerplexityAccountViewModel.cs`

Fields for `PerplexityAccountViewModel`:

- `Id`
- `Label`
- `MaskedSessionToken`
- `IsDefault`
- `IsDisabled` only if future parity needed; omit for first slice unless backend supports mutation

Note: backend `PerplexityAccountSettings.Disabled` exists, but current `AccountService` does not mutate it. UI should not expose disable toggle in this slice unless service contract is expanded deliberately.

### Providers section view

`src/TunnelAgent.Avalonia/Views/ProvidersView.axaml`

- remove in-page engine selector header
- gate current provider list and model list behind `IsCliProxyEngineSelected`
- add Perplexity mode content block:
  - engine summary
  - empty state
  - account list
  - add/remove/default actions
  - guidance text for obtaining session token manually

`src/TunnelAgent.Avalonia/Views/ProvidersView.axaml.cs`

- keep copy-endpoint behavior shared
- add Perplexity dialog confirm handler if dialog stays in this view
- no OAuth logic should run when Perplexity content active

### Configuration section

`src/TunnelAgent.Avalonia/Views/ConfigurationView.axaml`

- keep general app settings shared
- split engine configuration into parallel engine-specific sections with similar visual structure:
  - CLIProxy configuration section
  - Perplexity configuration section
- show focused engine section prominently; optional non-focused section may remain collapsed or visually secondary if both are shown together
- Perplexity section should mirror CLIProxy structure where concepts overlap:
  - engine version
  - release selection
  - install/update actions
  - package/status details
  - port controls
- Perplexity section MUST omit routing strategy because routing applies to CLIProxy provider distribution, not Perplexity session selection
- security/account copy in Perplexity section should reference session accounts rather than provider auth files
- port reset should use per-engine default port, not hard-coded `8317`

Potential design choice:

- keep both engine sections always visible in Configuration for side-by-side discoverability, but emphasize focused engine styling
- or show only focused engine section while preserving parallel structure in code
- leave global credential reset in security section, but relabel as global app-managed credentials only if semantics truly include both engine credential types

### Shared shell

`src/TunnelAgent.Avalonia/Views/MainWindow.axaml`

- add Providers sidebar submenus for `CLIProxyAPI` and `Perplexity`
- submenu selection updates `ActiveEngineId` and routes Providers content accordingly
- keep top-level Configuration entry, but have it reflect current focused engine

## Data Flow

### Startup

1. `SettingsService.LoadAsync()` loads persisted `ActiveEngineId` and engine runtime settings.
2. `EngineRegistryService` creates both managed engines.
3. `MainWindowViewModel.InitializeAsync()`:
   - initializes shared settings
   - initializes `ProviderCatalogService`
   - initializes Perplexity account catalog
   - initializes both engines
   - loads release list for focused engine
   - populates focused-engine-specific collections

Recommended behavior: initialize both engines enough to surface installed/latest status and support parallel running, but only fetch model list for CLIProxy when focused and running.

### Engine focus switch

1. User selects engine in UI.
2. `ActiveEngineId` updates and persists.
3. View model refreshes:
   - engine-derived labels/state
   - endpoint/port/config rows
   - visible management surface
   - release list for newly focused engine
4. Non-focused engine background events do not overwrite focused engine details.
5. Focus switch does not stop any running engine.

### Perplexity account add/remove/default

1. User opens add dialog in Perplexity mode.
2. View collects label + session token.
3. View model calls Perplexity account catalog service.
4. Service writes via backend `AccountService`.
5. Service raises changed event.
6. View model rebuilds `PerplexityAccounts` collection and status hints.

## UX Decisions

### Engine selection placement

Place engine choice in Providers sidebar submenus instead of in-page controls. Reason: engine choice changes navigation context for provider management and feels cleaner than ad-hoc page buttons.

### Perplexity account token handling

Show masked token only after save. Input dialog uses password masking. Do not redisplay raw token after persistence.

### Empty state

Perplexity empty state should explain:

- Perplexity engine uses saved WebUI session token
- at least one account required
- user can add multiple accounts and choose default

Avoid promising automatic login or validation.

### Available models section

Only show existing available-models block for CLIProxy mode. Perplexity backend health check uses `/v1/models`, but current app model fetch service is provider-catalog oriented and not specified for Perplexity mode.

## Tradeoffs

### Option chosen: focused-engine main view model + separate Perplexity account models

Pros:

- smallest change from current architecture
- preserves existing CLIProxy provider flow
- avoids semantic abuse of provider/account classes

Cons:

- `MainWindowViewModel` gets larger
- multiple conditional UI branches in same views

### Option rejected: force Perplexity into provider catalog

Rejected because Perplexity session accounts are not provider accounts in current UI model. Would create misleading controls like OAuth connect/quota/enable toggles.

### Option rejected: split whole app into separate windows/pages per engine

Rejected for first slice because too much shell duplication and navigation churn.

## Contracts

### Settings contract

Add field:

- `AppSettings.ActiveEngineId : string`

Behavior:

- invalid or empty value falls back to CLIProxyAPI
- switching focused engine persists immediately
- changing this value does not start or stop engines by itself

### Perplexity account catalog contract

UI-facing service methods:

- `IReadOnlyList<PerplexityAccountViewModel> List()` or model DTO equivalent
- `Add(string label, string sessionToken)`
- `Remove(string accountId)`
- `SetDefault(string accountId)`
- `event EventHandler Changed`

No token validation required in contract.

### Main window contract

Derived properties must always reflect focused engine for shared header/actions:

- `EndpointUrl`
- `InstalledVersion`
- `LatestVersion`
- `UpdateAvailable`
- `EngineStatusText`
- release selector content
- default port reset behavior

If Configuration renders both engine sections simultaneously, each section also needs engine-scoped derived properties so one engine's version/status/port state does not bleed into other section.

## Testing Strategy

Current OpenSpec config reports no reliable runner. Design for targeted unit coverage where feasible, plus manual verification.

Focus tests:

- settings persistence for `ActiveEngineId`
- Perplexity account add/remove/default logic through catalog service
- focused engine switching updates derived view model state without stopping other running engines
- port reset uses selected engine default port
- Perplexity configuration section omits routing strategy while CLIProxy section still shows it

Likely test files:

- new tests for Perplexity account catalog service
- new tests for engine-aware main view model state transitions

Manual verification:

- switch focused engine without app restart
- start/stop each engine from UI while other engine may remain running
- add/remove/default Perplexity account
- verify CLIProxy provider list unchanged after returning from Perplexity mode

## Rollout

1. Add settings + service abstractions.
2. Refactor main view model to active-engine model.
3. Add Perplexity UI models and view branches.
4. Wire configuration page to active engine.
5. Manual regression pass across both engines.

## Risks

- current `ResetPort()` hard-codes `8317`; missing this would silently break Perplexity defaulting.
- initializing both engines may increase startup work, but parallel-run support makes one-engine-only assumptions unsafe.
- two configuration sections can accidentally drift if labels/actions are copied instead of engine-scoped through reusable bindings or templates.
- global reset-credentials semantics may need explicit copy review so Perplexity users do not think session accounts are covered unless they are.

## Open Questions

- Should Perplexity session accounts participate in global credential reset, or remain separate explicit removals only?
- Should non-focused engine continue background update checks, or should only focused engine initialize release metadata?
- Should Perplexity account actions stay only in Providers page, or should Configuration include shortcuts/read-only summaries too?
