# Proposal: add-perplexity-ui

## Problem Statement

Tunnel Agent already contains backend support for Perplexity WebUI Scraper, including engine registration, release management, process lifecycle, settings persistence, and Perplexity account storage. Current Avalonia UI still assumes CLIProxyAPI-only workflow: startup wiring, provider management, status copy, and engine configuration all target CLIProxyAPI. Users cannot discover, install, configure, or operate Perplexity engine from desktop app.

## Why Now

Perplexity backend slice is present enough to expose first-class UI workflow. Without UI, feature is effectively hidden and cannot be validated end-to-end by normal users.

## Intent

Expose Perplexity as supported engine in desktop app with focused management UI for engine selection, Perplexity account/session setup, and engine-specific status/configuration.

## Scope

- Add UI affordance to choose active engine between CLIProxyAPI and Perplexity WebUI Scraper.
- Add Perplexity-specific management surface for stored accounts/session tokens.
- Adapt status, endpoint, and engine configuration copy/actions to selected engine.
- Preserve existing CLIProxyAPI provider-management workflow when CLIProxyAPI remains selected.

## Non-Goals

- Changing backend process, download, or release-management behavior for either engine.
- Reworking CLIProxyAPI provider/account UX beyond what active-engine selection requires.
- Adding Perplexity browser login, quota fetching, or token validation if backend does not already provide it.
- Defining implementation tasks or technical design details in this phase.

## Affected Areas

- `src/TunnelAgent.Avalonia/App.axaml.cs`
- `src/TunnelAgent.Avalonia/ViewModels/MainWindowViewModel.cs`
- `src/TunnelAgent.Avalonia/Views/MainWindow.axaml`
- `src/TunnelAgent.Avalonia/Views/ProvidersView.axaml`
- `src/TunnelAgent.Avalonia/Views/ConfigurationView.axaml`
- Perplexity-specific view/viewmodel additions under `src/TunnelAgent.Avalonia/Views` and `ViewModels`
- Supporting app services that bridge active-engine selection and Perplexity account persistence

## Capabilities

### New Capabilities

- `perplexity-ui`

## User-Visible Outcome

User can switch desktop app into Perplexity mode, manage Perplexity accounts needed by scraper engine, and operate/install selected engine without leaving app or editing files manually.

## Risks

- Current app startup and main view model are tightly bound to CLIProxyAPI `EngineService`; active-engine abstraction may touch multiple UI surfaces.
- Existing provider list is CLIProxy-centric; forcing Perplexity controls into same surface may create confusing mixed mental model.
- Backend account API currently proves add/remove/default persistence, but not richer validation or sync; UI must avoid implying unsupported guarantees.

## Rollback

Revert change directory and UI wiring. Existing CLIProxyAPI-only flow remains baseline.

## Success Criteria

- Perplexity engine is discoverable and selectable in desktop UI.
- User can add, inspect, remove, and choose default Perplexity account/session entry from UI.
- Status, port, install/update actions, and empty states reflect currently selected engine.
- CLIProxyAPI provider workflow remains intact when CLIProxyAPI is selected.
