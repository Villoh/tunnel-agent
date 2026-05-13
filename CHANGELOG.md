# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-05-14

### Added

#### Provider Catalog
- `ProviderCatalogService` — orchestrates the full provider lifecycle: auth-dir sync, add/remove accounts, enable/disable, config.yaml rewrite
- `OAuthTokenDetector` — reads `~/.cli-proxy-api/{prefix}-{email}*.json` files to detect connected OAuth providers and parse account metadata (email, plan badge)
- `AuthFileWatcher` — `FileSystemWatcher` with 400 ms debounce over `~/.cli-proxy-api/` to react to new/removed token files in real time
- `CustomProviderCredentialStore` — reads/writes `openai-compat-{id}-{uuid}.json` credential files, compatible with CLIProxyAPIPlus format
- `OAuthService` — launches `cli-proxy-api -<provider>-login` to trigger OAuth browser flow; handles Gemini stdin newline, Codex keepalive, Copilot device-code extraction
- `QuotaFetchService` — fetches live quota from provider APIs using the `access_token` in local token files:
  - **Claude**: `api.anthropic.com/api/oauth/usage` → `five_hour` + `seven_day` utilization %
  - **Codex**: `chatgpt.com/backend-api/wham/usage` → `primary_window` + `secondary_window` used %
  - **Copilot**: `api.github.com/copilot_internal/user` → `premium_interactions` + `chat` quota
- `ModelFetchService` — calls `/v1/models` on the running proxy and groups results by `owned_by`

#### Providers UI (VibeProxy-inspired redesign)
- Provider rows: `[Toggle] [Icon] [Name + colored status line] [action buttons] [›]`
  - Status sub-line: green when connected, orange when disabled, muted otherwise
  - `ConnectedSubText`: "N connected accounts · Round-robin w/ auto-failover"
  - `[disabled]` chip badge when provider is toggled off
- **Toggle pill** with animated sliding white thumb (RotateTransform, CSS-like transition)
- Toggle auto-disabled when no accounts exist; auto-off when last account is removed
- **Expand chevron** (`›` / `˅`) — only visible when provider has accounts; expands account panel
- **`+` icon button** — single entry point for OAuth connect/add-account (always visible for OAuth providers)
- **`LogOut` icon button** — disconnect all accounts (visible only when connected); consistent with per-account disconnect
- Connecting chip badge shown while OAuth flow is in flight
- Custom providers: "Add Account" button opens inline dialog

#### Expanded Account Panel
- Per-account card with email/label, plan badge (PLUS/PRO/FREE), masked API key chip
- **Quota bars**: `ProgressBar` (green fill, 0–1 range) with title, "N% used" label, "Resets in Xd Yh" countdown
- `HasQuota` properly reactive via `CollectionChanged` on `QuotaBars` collection
- **Refresh `↺` button** per account — re-fetches quota from provider API with spinning animation (`RotateTransform.Angle` 0→360, 0.7 s linear)
- Enable/disable toggle per account — **persisted to disk** on change:
  - OAuth accounts: patches `"disabled"` field in `{prefix}-{email}*.json`
  - Custom accounts: patches `openai-compat-*.json` via `CustomProviderCredentialStore`
- `LogOut` icon to remove/disconnect individual account

#### Available Models
- Section shows "Start the server to see available models" (`ServerOff` icon) when proxy is stopped
- When server starts: auto-fetches `/v1/models`, groups by provider (`owned_by`), shows model count per group
- Expandable model groups with model ID (monospace), context hint, provider chip, auth-kind chip
- Total model count updates reactively via `CollectionChanged` on `AvailableModelGroups`

### Changed

- `AppSettings` extended with `ProviderSettings` and `ProviderAccountSettings` classes
- `EngineConfigService` rewrote config generation to produce real `oauth-excluded-models` and `openai-compatibility` YAML blocks from live provider state
- `MainWindowViewModel` wires `ProviderCatalogService`; providers loaded from catalog (not seeded)
- `App.axaml.cs` instantiates and passes `EngineConfigService` + `ProviderCatalogService` to the ViewModel
- `ProvidersView` header renamed "Services" to match VibeProxy

### Fixed

- Provider row `Button` wrapper disabled all children (toggle, connect) — replaced with `Grid` + name-only clickable `Button.provider-row-name`
- Grey background on unconnected provider rows — forced `Transparent` on `ContentPresenter` in base state
- `window.close()` removed from OAuth callback page (doesn't work in non-script-opened tabs)
- OAuth callback page `Setter.Value` crash (`Cannot use a control as a Setter value`) — replaced Content setter with `ControlTemplate`
- `RenderTransform` animation crash — replaced string value with `RotateTransform` + `(RotateTransform.Angle)` property path
- Duplicate "connecting…" chip (appeared in both name area and action buttons)
- `TotalAvailableModelCount` always returning 0 — wired `CollectionChanged` on `AvailableModelGroups`
- `RemoveAccountAsync` for OAuth accounts (was passing empty `ApiKey`) — now uses `Email` to find token file
- Account enable/disable toggle not persisted across restarts — wired `IsDisabledChanged` event to disk write
- Add account tooltip showing `True`/`False` — replaced converter binding with static string
- Quota bar spacing (no gap between bars) — increased `Margin` to `0,6,0,0`
- Refresh button `IsRefreshing` not always restored — dispatched to UI thread in `finally` block

[0.2.0]: https://github.com/Villoh/tunnel-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Villoh/tunnel-agent/releases/tag/v0.1.0
