# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **xAI provider support**: xAI (Grok) is now available as an OAuth provider alongside the rest. Includes login via `--xai-login`, token file detection, custom Grok logo, and correct "OAuth" auth kind label in Available Models.
- **Removed unused SVG assets**: `cursor-agent.svg` (leftover from when Cursor was an agent) and `kimi.svg` (Kimi uses an inline path in `ProviderIconRegistry`) have been deleted.

### Fixed

- **Refresh buttons show grey background on press**: quota, agents, and logs refresh buttons now use `Click` handler instead of `Command` binding, preventing the automatic disable state that caused a grey square to appear during execution. The spin icon already provides visual feedback. All three buttons now use the `icon` style for consistent appearance.
- **Plan badge inconsistent casing**: all providers now use title-case for plan badges (e.g. `PRO` → `Pro`, `PLUS` → `Plus`). A `ToPlanBadge` helper normalises any API string word-by-word. Kiro's `KIRO FREE` / `KIRO PRO` prefix is stripped since the provider name is already shown in the UI, leaving just `Free` / `Pro`.
- **Cursor plan badge missing**: `FetchCursorAsync` now calls `GetPlanInfo` to retrieve `planName` (e.g. `Pro`, `Ultra`) and sets it as the plan badge on each refresh.
- **Gemini CLI hides quota groups not included in plan**: groups with `remainingFraction=0` and no reset date (epoch `1970-01-01` treated as null) are now hidden — this indicates the plan does not include the model tier, not that the quota is exhausted.

## [0.5.5] - 2026-06-07

### Fixed

- **Gemini CLI quota aligned with Quotio spec**: `loadCodeAssist` now called first to extract plan badge (`paidTier` takes priority over `currentTier`). Quota groups restructured to three tiers: `Gemini Flash Lite`, `Gemini Flash`, and `Gemini Pro`, each with preferred model IDs per Quotio's group definitions. `_vertex` model ID suffix stripped. `resetTime` of `1970-01-01` (epoch) treated as no data. Buckets with null `remainingFraction` are skipped.
- **Copy All copies raw content for single-file configs**: filename and path headers are now only prepended when copying multiple config files at once (e.g. Codex `config.toml` + `auth.json`); single-file configs copy the raw content directly.
- **Select All checkbox is now two-state**: the indeterminate third state added no value and made the toggle confusing; it now cycles between checked and unchecked only.
- **Antigravity quota aligned with OpenUsage/Quotio spec**: `daily-cloudcode-pa.googleapis.com` is now tried first with `cloudcode-pa.googleapis.com` as fallback. Model bars now use `displayName` from the API response instead of regex grouping, giving accurate per-model labels (e.g. `Claude Sonnet 4.6 (Thinking)`, `Gemini 3.1 Pro (High)`). Internal models (`isInternal: true`) and models with empty `displayName` are filtered out. Duplicate display names are deduplicated keeping the lowest `remainingFraction`. Gemini 2.x model IDs blacklisted as they duplicate newer entries. `loadCodeAssist` is now called first to obtain the `cloudaicompanionProject` ID, which is passed to `fetchAvailableModels` — without it the API returns integer `1` for all fractions; with it real usage fractions are returned. Plan badge derived from `currentTier.id` (`free-tier` → `Free`, etc.). Models with `null` `remainingFraction` (e.g. Claude/GPT on free plan) are hidden rather than shown as 0%.
- **Codex quota aligned with OpenUsage spec**: `ChatGPT-Account-Id` header now sent when available (improves quota accuracy). `code_review_rate_limit` window added as a bar when present. `credits` balance shown in plan badge when `has_credits=true`. Email populated from API response. Proactive token refresh when `last_refresh` > 8 days or near expiry; reactive refresh on 401/403. Token refresh uses `POST auth.openai.com/oauth/token` with form-encoded body per spec.
- **Claude quota aligned with OpenUsage spec**: added `seven_day_opus`, `seven_day_sonnet`, and `seven_day_omelette` rate-limit windows (plan-dependent, optional); windows with `utilization=0` and no `resets_at` are hidden. `extra_usage` overage is shown in the plan badge only when `used_credits > 0`. `subscriptionType` from `~/.claude/.credentials.json` is used as the plan badge. Proactive token refresh 5 minutes before expiry; reactive refresh on 401/403.
- **Trae shows wrong quota metrics for dollar-based plans**: the API response includes `is_dollar_usage_billing: true` for free-tier accounts, which use `basic_usage_amount`/`basic_usage_limit` (dollar spend) and `auto_completion_limit` — not Premium Fast/Slow or Advanced Models. The fetcher now branches on this flag and displays `Free plan ($x.xx/$y.00)` and `Autocomplete` bars instead.
- **Trae username not shown on Windows**: `ReadTraeTokenFromLogs` now also extracts `userName` from `userInfoChange` log lines and uses it as the account identifier when no email is available from `storage.json`.
- **Hide Sensitive Info does not mask non-email identifiers**: `MaskEmailAddress` only masked values containing `@`, leaving opaque IDs (e.g. Kiro `userId`) fully visible. Non-email values are now masked as first 4 characters followed by bullets.
- **Kiro live API request always failing when token expired**: `User-Agent` header containing commas (`m/N,E`) caused `Headers.Add()` to throw `FormatException` silently aborting the entire request. Fixed with `TryAddWithoutValidation`. `nextDateReset` in the live API response is a JSON float (e.g. `1.782864E9`, unix seconds), not a string — now correctly parsed and converted to ISO 8601. `currentUsageWithPrecision` / `usageLimitWithPrecision` are now preferred over the truncated integer fields when present.
- **Kiro quota aligned with OpenUsage spec**: auth token field names corrected to camelCase (`accessToken`, `refreshToken`) matching the observed `kiro-auth-token.json` shape. Added `profile.json` fallback for `profileArn`. `ScanKiro` and `FetchKiroAsync` now read Kiro's SQLite usage cache (`state.vscdb` → `kiro.resourceNotifications.usageState`) first, enrich plan title and overage status from the latest `q-client.log` `GetUsageLimitsCommand` response, and only fall back to the live API when the local snapshot is missing or stale (>10 min). `subscriptionInfo.subscriptionTitle` is applied as the plan badge; `overageConfiguration.overageStatus` is appended. Auth-mode headers (`TokenType: EXTERNAL_IDP`, `redirect-for-internal`) are sent for non-social accounts. Social token refresh URL fixed to always use `us-east-1`. Local and live API breakdown shapes unified in `ParseKiroBreakdownList`.
- **Trae quota aligned with Quotio**: entitlement fallback now uses the first item in `user_entitlement_pack_list` when none has `status == 1`. `product_type` mapping corrected (`0`→`"Free"`, `1`→`"Pro"`, `2`→`"Team"`, `3`→`"Builder"`); unknown types no longer show a misleading `"FREE"` badge.
- **Trae auth on Windows**: `storage.json` values are Electron `safeStorage`-encrypted on Windows and cannot be decrypted outside the Electron process. `ScanTrae` and `FetchTraeAsync` now try plain JSON parse first (works on macOS) and fall back to scanning the most recent `completion.log` for a `Cloud-IDE-JWT` bearer token when the value is encrypted.
- **Cursor quota not detected**: `Immutable=True` is not supported by `Microsoft.Data.Sqlite` and was silently throwing an exception inside the `catch {}` block, causing `ScanCursor` and `FetchCursorAsync` to always return not-detected. Replaced with `Mode=ReadOnly`.

### Removed

- **Quota tab removed from Providers**: the "Quota" tab and embedded Quota Providers panel (Cursor, Kiro, Trae) have been removed from the Providers section. Cursor, Kiro, and Trae remain exclusively in the Quota window.

### Added

- **Cursor in Providers → Quota**: Cursor is now detected via `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (SQLite) and shown alongside Kiro and Trae in the Quota section. Quota usage (plan requests and on-demand) is fetched from `POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage` using the stored bearer token, with automatic token refresh via the OAuth refresh token.
- **Kiro added to IDE Quota Tracking**: Kiro now appears in the Quota SlidingTabBar alongside Cursor and Trae.

### Removed

- **Cursor Agent removed from agent configuration**: Cursor is an IDE, not a CLI agent. It has been removed from the agents catalog and all related shell-export configuration logic. It is now correctly placed in Providers → Quota (monitor-only).

### Fixed

- **`ModelEntry.ApiKey` stores raw env var name**: API key references in `ModelEntry` are now stored as plain variable names (e.g. `TUNNEL_AGENT_CLIPROXY_API_KEY`) and each agent's config builder applies its own format — OpenCode wraps with `{env:VAR}`, Droid and Pi wrap with `${VAR}`. Previously, the OpenCode format was applied at creation time and incorrectly propagated to all agents.
- **Fallback preview shows env var reference instead of raw key**: when the engine is not running and no models are selected, the manual config preview for OpenCode, Pi, and Factory Droid now shows the environment variable placeholder (`{env:TUNNEL_AGENT_CLIPROXY_API_KEY}` / `${TUNNEL_AGENT_CLIPROXY_API_KEY}`) instead of the raw API key value.

## [0.5.4] - 2026-06-05

### Added

- **Logs dashboard**: added a dedicated Logs section with Requests and Proxy Logs tabs, toolbar actions, search, provider filtering, CSV/log export, and Quotio-inspired dark UI styling.
- **Requests history**: Requests now show parsed CLIProxyAPI traffic with full timestamps (`yyyy-MM-dd HH:mm:ss`), newest-first ordering, provider labels, status badges, latency, search, provider filtering, and 25-entry pagination over the full in-memory history.
- **Proxy Logs view**: raw CLIProxyAPI logs are shown in a dedicated tab with search and a 100-line cap for performance.
- **Management API + file fallback for logs**: Logs use `GET /v0/management/logs` while CLIProxyAPI is running and fall back to reading local `main.log` when the server is stopped; manual refresh works in both modes.
- **Log actions**: refresh triggers a manual poll with a spin animation, the eraser clears the in-memory view, downloads export Requests as CSV and Proxy Logs as `.log`, and the trash action deletes the underlying log file with a global confirmation dialog.
- **Logs auto-refresh settings**: added CLIProxy configuration controls for enabling/disabling logs auto-refresh and selecting the polling interval; auto-refresh is disabled by default.
- **CLIProxy management key support**: generated and persisted a UUID-style management key and wrote `remote-management.secret-key` plus `logging-to-file: true` into the CLIProxy config.

### Fixed

- **CLIProxyAPI and Perplexity credential updates**: adding, setting default, or removing credentials now runs environment-variable writes off the UI thread while clearing both user and process values, preventing stale defaults from being re-added during refresh.
- **OpenCode apiKey env placeholders**: OpenCode provider configuration now writes API keys with `{env:ENV_VARIABLE}` placeholders instead of shell-style `${ENV_VARIABLE}` placeholders.
- **Model selector always shown in bulk agent config**: opening the multi-agent configuration dialog no longer inherits the hidden model selector state from a previously opened single-agent dialog (Claude Code, Codex, Gemini CLI, Amp).

## [0.5.3] - 2026-06-05

### Fixed

- **Agent icons in config popup follow theme**: the configuration popup (single-agent summary and bulk agent picker) now uses the theme-aware `Icon` property instead of the static `SvgImageConverter`, so OpenCode, Pi, Factory Droid, and Cursor Agent icons correctly switch between dark and light fills when the theme changes.
- **Single instance: debug and release no longer share mutex**: the mutex and named pipe used for single-instance enforcement now use distinct names in debug builds (`-Debug` suffix), preventing a debug session from activating the release instance and vice versa.

## [0.5.2] - 2026-06-04

### Fixed

- **Single instance enforcement**: launching a second instance of the app while one is already running now brings the existing window to the front (restoring it if minimized or hidden to tray) instead of opening a duplicate window.

## [0.5.1] - 2026-06-03

### Fixed

- **App update download progress**: clicking "Download" in the app update toast now navigates to the General settings section and shows a progress bar while downloading. Previously there was no feedback and the download happened silently in the background.
- **App update applies automatically**: once the download completes the update is applied and the app restarts without requiring a second button press.
- **Configuration badge on app update**: the orange dot on the Configuration sidebar item now also appears when a Tunnel Agent app update is available, not only when an engine update is pending.

## [0.5.0] - 2026-06-03

### Added

- **Sidebar collapse/expand animations**: smooth width transition (220ms `CubicEaseInOut`) when toggling the sidebar, with a fade out/in effect (180ms) on all text labels, badges, and status indicators. The toggle button icon flips horizontally with a 220ms scale transition instead of swapping between two separate icons.
- **Configuration badge**: update dot replaced with an animated pulse indicator (matching the engine status style) using `WarnBrush` (orange) for visibility in both selected and unselected states.

- **Dark theme**: Vercel-inspired pure black backgrounds (`#000000` window, `#0A0A0A` sidebar, `#111111` cards) with subtle `#1A1A1A` borders, replacing the previous blue-grey palette.
- **Agent icon theme-aware rendering**: OpenCode, Pi, Factory Droid, and Cursor Agent SVG icons (which have black fills) now automatically invert to white in dark mode and revert to black in light mode via CSS injection (`path { fill: ... }`). Updates live when the theme is switched without restarting.

- **Engine auto-start**: each engine (CLIProxyAPI and Perplexity WebUI Scraper) now has an "Auto-start" toggle in its ENGINE settings card. When enabled, the engine starts automatically on app launch. The setting is persisted per-engine in `settings.json` under `Engines[].AutoStart`.

- **Amp CLI agent support**: full integration including binary detection (`~/.amp/bin/amp.exe` on Windows), automatic configuration of `~/.config/amp/settings.json` (`amp.url`) and `~/.local/share/amp/secrets.json` (`apiKey@url`), access token field with eye icon in the config dialog, and `ampcode` block written directly to `proxy-config.yaml` (`upstream-url` + `upstream-api-key`).
- **Amp CLI model selector hidden**: Amp manages its own models internally; no model multiselect shown in the config dialog.
- **Amp access token preserved on yaml regeneration**: `ConfigService` reads the existing `upstream-api-key` from `proxy-config.yaml` and preserves it whenever the config is regenerated, even if not stored in `settings.json`.
- **Agent config confirmation paths on separate lines**: multiple config file paths now display one per line instead of joined with ` + `.

### Fixed

- **Update success banner scoped per engine**: updating or installing a specific version of one engine (CLIProxyAPI or Perplexity) no longer shows the "Successfully updated" banner on both configuration tabs simultaneously. Each tab now tracks its own success state independently.
- **Engine update toast shown on startup for any engine**: the "update available" notification at the bottom of the screen now appears for both CLIProxyAPI and Perplexity regardless of which config tab is focused. Previously the toast was only triggered for the currently focused engine, so updates detected on the background engine were silently ignored.

- **CLIProxy API keys and default key removed from `settings.json`**: keys are now stored exclusively in `proxy-config.yaml` (`api-keys:`) and the default key in the `TUNNEL_AGENT_CLIPROXY_API_KEY` user environment variable. One-time migration runs on startup to move legacy keys from `settings.json` to the yaml.
- **`TUNNEL_AGENT_CLIPROXY_API_KEY` read from user environment registry**: `UserEnvironmentService.Get` now reads from `EnvironmentVariableTarget.User` first, so the variable is detected in the same session it was set without requiring a restart.
- **First CLIProxy API key automatically set as default**: adding the first key (or any key when no default is set) now automatically marks it as default and sets the env var.
- **CLIProxy API key visible in popup on startup**: env var key missing from yaml is automatically added to keep both in sync.

- **Amp `secrets.json` always written**: previously skipped when no CLIProxy API key was configured; now always written with `no-key` as fallback so Amp does not start the OAuth login flow.
- **Amp access token not stored in `settings.json`**: the `upstream-api-key` is written and read directly from `proxy-config.yaml`, avoiding redundant storage of a sensitive value.

### Changed

- **Traffic-light buttons**: icons (−, ⤢, ×) now fade in on hover using a custom `ControlTemplate` with a `Border` opacity transition (150ms).
- **Gemini CLI agent configuration**: apply now saves `GOOGLE_GEMINI_BASE_URL` and `GEMINI_API_KEY` as persistent user environment variables (instead of showing a manual shell export). Revert removes both variables. On Windows, `WM_SETTINGCHANGE` is broadcast so newly spawned processes pick up the change.
- **Gemini CLI configured detection**: uses `GOOGLE_GEMINI_BASE_URL` env var presence to detect whether the agent is configured.

### Fixed

- **Quota tab navigation**: switching between any provider tab (including Kiro and Trae) now correctly updates the content view. Kiro/Trae are registered as `ProviderViewModel` placeholders when not yet detected, keeping navigation consistent with all other providers.
- **Edit Perplexity label dialog**: Escape and Enter now work regardless of focus; clicking outside the dialog closes it.

### Changed

- **Pi agent icon**: changed from white to black.
- **README**: added Windows-only warning with note welcoming cross-platform contributions, fixed engine section headers to link to their respective repositories, updated Usage section to reflect automatic engine installation on startup and clarified agent configuration flow.
- **README**: removed GitHub Copilot from the supported providers table; corrected Codex, Kimi, and Antigravity auth method from API key to OAuth.
- **GitHub Copilot removed**: dropped all Copilot support from code and UI — the `--github-copilot-login` flag does not exist in CLIProxy.
- **Traffic-light buttons hover**: replaced the grey Avalonia default hover with a color-preserving style that keeps the circle color and fades in the icon.
- **Gemini CLI proxy variables**: switched from `CODE_ASSIST_ENDPOINT` to `GOOGLE_GEMINI_BASE_URL` + `GEMINI_API_KEY` (API key mode), matching the CLIProxyAPI documentation.
- **Gemini CLI**: model selector hidden in agent config dialog (Gemini CLI only supports Gemini models via the proxy).

## [0.4.5] - 2026-06-01

### Added

- **Factory Droid `displayName`**: resolved from OpenRouter `name` field (provider prefix stripped), with local formatting fallback. All models include `(Tunnel Agent)` suffix; Perplexity models include `(Tunnel Agent - Perplexity)`.
- **Pi two-provider split**: `tunnel-agent-cliproxy` and `tunnel-agent-perplexity` providers written to `models.json` with correct `baseUrl`, `apiKey` (env var refs), and per-model `name`, `contextWindow`, and `input` fields.
- **OpenCode two-provider split**: `tunnel-agent-cliproxy` and `tunnel-agent-perplexity` providers written to `opencode.json` using `@ai-sdk/openai-compatible`, with `litellmProxy: true`, env var `apiKey` refs, and per-model `name`.
- **Perplexity models in agent config**: when the Perplexity engine is running, its models now appear in the agent configuration dialog alongside CLIProxy models.
- **`TUNNEL_AGENT_PERPLEXITY_TOKEN` env var**: automatically set/updated/removed in user environment variables when a Perplexity account is added, set as default, or removed. Factory Droid config uses `${TUNNEL_AGENT_PERPLEXITY_TOKEN}` instead of the raw session token.
- **`TUNNEL_AGENT_CLIPROXY_API_KEY` env var**: automatically set/updated/removed when the default CLIProxy API key changes. Factory Droid config uses `${TUNNEL_AGENT_CLIPROXY_API_KEY}` instead of the raw key.
- On Windows, `WM_SETTINGCHANGE` is broadcast after env var changes so newly spawned processes pick up the new values without logoff.
- **Claude Code**: `ANTHROPIC_AUTH_TOKEN` and `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1` written to `~/.claude/settings.json` env block. `ANTHROPIC_BASE_URL` no longer includes `/v1`.

### Changed

- **Dual engine model fetch**: `ActiveEngine`/`ActiveEngineId` removed — each engine (`CliProxy`, `Perplexity`) now maintains its own independent model collection and fetch lifecycle. Both engines can be running and serving models simultaneously.
- **Providers model list**: now shows only the models of the currently selected engine tab instead of a combined list.
- **Factory Droid Perplexity models**: now use the correct Perplexity engine endpoint (`http://127.0.0.1:8327/v1`) instead of the CLIProxy endpoint.
- **Codex CLI config**: writes `config.toml` with `model_provider = "cliproxyapi"` and `wire_api = "responses"`, plus `auth.json` with `auth_mode: "apikey"` and `OPENAI_API_KEY`. Both files shown in the apply result.
- **Codex CLI**: model selector hidden in agent config dialog (Codex only supports one model at a time, selected at runtime).
- **Claude Code**: model selector hidden in agent config dialog (uses alias-based model selection at runtime).
- Models expander label no longer uses an emdash (`Models N of N selected`).

### Fixed

- Models in agent config dialog were empty on open if engines were already running before the dialog was opened.
- UI no longer freezes when adding, removing or changing the default CLIProxy API key or Perplexity account — env var writes and `WM_SETTINGCHANGE` broadcast now run on a background thread.
- UI no longer freezes when navigating to the Agents section — SVG icons now load on the UI thread at background priority, and binary/config detection runs entirely on the thread pool.
- GitHub releases now correctly show "What's Changed", contributors and "Full Changelog" sections.
- Release workflow now always commits the version bump back to `main` (previously skipped when triggered from CHANGELOG).

## [0.4.4] - 2026-06-01

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).
- **Pi input modalities**: `input` is now written per-model based on OpenRouter's `input_modalities` — `["text", "image"]` for vision-capable models, `["text"]` for text-only.
- **Pi preview**: the manual preview now also resolves `contextWindow` and `input` from OpenRouter, so what you see matches what Apply writes.

### Fixed

- **Factory Droid config**: config file corrected to `~/.factory/settings.json` (was `config.json`), field names updated to camelCase (`displayName`, `baseUrl`, `apiKey`), and provider changed to `generic-chat-completion-api` per official docs.
- **Factory Droid provider inference**: provider is now resolved from the model's `owned_by` field (from `/v1/models`) — `anthropic` (with `/v1` stripped) for Anthropic models, `openai` for OpenAI models, `generic-chat-completion-api` for everything else.
- **Factory Droid apiKey**: `apiKey` is now always written (using `"no-key"` as fallback) since Factory Droid requires the field.
- **Agent config dialog**: dialog height is now `MaxHeight` instead of fixed, so it shrinks to fit content after applying.
- **Dialog keyboard shortcuts**: Escape closes and Enter confirms in the Agent Config, Manage Keys, and Add Perplexity Account dialogs.
- **Dialog focus**: dialogs now receive focus automatically on open so keyboard shortcuts work immediately without clicking first.
- **Click outside to close**: clicking the backdrop now closes the Agent Config, Manage Keys, and Add Perplexity Account dialogs.

## [0.4.3] - 2026-06-01

### Added

- **App update UI**: Configuration → General now shows the installed Tunnel Agent version, a "Check" button, and a toggle for auto-check on startup.
- **Update toast**: a non-blocking toast appears when a new Tunnel Agent version is available, with "Download" and "Restart & Install" actions.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Scoop bucket now uses the portable zip instead of the installer — simpler install with no UAC prompt.
- `latest.json` release asset now includes the portable zip hash (`portable.sha256`) alongside the installer hash.
- Section spacing in Configuration → General matches the CLIProxy section.

## [0.4.2] - 2026-06-01

### Added

- Scoop bucket support — `scoop bucket add villoh https://github.com/Villoh/scoop-bucket` then `scoop install villoh/tunnel-agent`.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- App name corrected to "Tunnel Agent" (with space) in installer, GitHub Releases title, and all user-visible UI strings.

## [0.4.1] - 2026-06-01

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- CI test suite now runs on Ubuntu (faster, cheaper) with Windows reserved for the build and release jobs.
- Platform-specific tests (`Win32Exception`, binary path `.exe`) correctly skip on Linux instead of failing.
- Blocking `GetAwaiter().GetResult()` calls in tests replaced with `async/await` (xUnit1031).
- `QuotaProviderCount` tests updated to match current account-based semantics.
- Unused `[Theory]` parameters removed from `EngineService_ServerState_MapsCorrectly` (xUnit1026).
- CI runner pinned to `windows-2025` to avoid GitHub's `windows-latest` redirect notice.

## [0.4.0] - 2026-05-30

### Added

- **GitHub Copilot username display**: reads the `username` field from `github-copilot-*.json` auth files so accounts show the GitHub username instead of falling back to filename parsing.
- **Hide sensitive information** setting in Configuration → General: masks all account email addresses with bullet dots (`•••@•••`) to keep them private on shared screens.
- **Quota view**: dedicated sidebar section to track quota usage for CLIProxyAPI providers (Claude, Codex, GitHub Copilot, Gemini CLI, Antigravity) and standalone IDE accounts (Kiro, Trae).
- **Quota providers tab** in Providers view: read-only tab showing detected standalone quota providers (Kiro, Trae) that are not managed by CLIProxyAPI or Perplexity.
- **Gemini CLI quota fetching**: direct API calls to `cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota` with OAuth token refresh support.
- **Antigravity quota fetching**: direct API calls to `cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels` with OAuth token refresh support.
- **Kiro quota fetching**: direct API calls to AWS CodeWhisperer usage API with Social/IdC token refresh support.
- **Trae quota fetching**: direct API calls to Trae entitlement API with Cloud-IDE-JWT authentication.
- **Custom SVG icons**: `SlidingTabBar` now supports `CustomIconData` property for SVG path rendering; `ProviderIconRegistry` includes custom icons for Antigravity, Kiro, and Trae.
- **Global quota refresh**: single refresh button in Quota view header refreshes all providers with active accounts.
- **Initial quota load**: Quota view auto-loads quota data on first open if accounts exist but no data is loaded.
- Agents page with CLI tool detection, installed/configured state, SVG icons, documentation links, individual setup, and multi-agent setup.
- Agent configuration support for Claude Code, Codex, Gemini CLI, Amp, OpenCode, Pi, Factory Droid, Cursor Agent, and Aider.
- Model picker in the Agents setup dialog with search, tri-state select-all for visible results, and dynamic manual config previews.
- CLIProxyAPI key management UI with optional default key selection, removable keys, and generated agent configs that omit auth when no default key exists.
- Eye/eye-off reveal toggles for CLIProxy API key, custom provider API key, and Perplexity session token inputs.
- SVG agent assets for Amp, Claude Code, Codex, Cursor Agent, Factory Droid, Gemini CLI, OpenCode, and Pi.

### Changed

- **GitHub Copilot quota fetching**: fixed API response parsing to use the real field structure (`quota_snapshots.{chat,completions,premium_interactions}` with `percent_remaining`/`remaining`/`entitlement`; `limited_user_quotas`+`monthly_quotas` for Free/Individual plans); added reset date from `quota_reset_date_utc`; corrected plan badge detection via `copilot_plan`+`access_type_sku`; updated request headers to `Accept: application/vnd.github+json` + `X-GitHub-Api-Version: 2022-11-28`.
- **Quota UI moved** from Providers account cards to dedicated Quota view; Providers now focuses solely on account management (add/remove/enable/disable/connect/disconnect).
- **Quota refresh policy**: tab switching no longer triggers refresh; explicit refresh actions (global button, per-account button) or initial load only.
- **Shorter tab labels**: "GitHub Copilot" → "Copilot", "Gemini CLI" → "Gemini" to fit 7 tabs in Quota view.
- **Provider icon colors**: Kiro uses `#9046FF` (Kiro purple), Trae uses `#32F08C` (Trae green), Antigravity uses `#7C3AED` with custom butterfly icon.
- **icon-btn style**: added `Foreground` setter using `FgBrush` and `:pressed` state for consistent button feedback.
- Agents detection now runs in the background, uses parallel detection, and exposes a compact spinning refresh action.
- Agents setup now uses icon-only gear actions for single and bulk configuration.
- OpenCode, Pi, and Factory Droid setup now register selected models dynamically instead of relying on static model slots.
- Factory Droid config now writes `customModels` entries per model and migrates legacy `custom_models` entries.
- OpenCode config now omits hardcoded context/output/vision limits and lets OpenCode use provider/model defaults.
- CLIProxy API keys are now optional: empty key list means no `api-keys` entry in `proxy-config.yaml` and no bearer header from Tunnel Agent.
- `settings.json` no longer persists provider/runtime state (`Providers`, `PerplexityAccounts`); provider intent is loaded from `proxy-config.yaml` where practical and credentials remain file-backed.
- Agents setup dialogs use improved scrolling, spacing, and compact controls.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- **Qwen provider removed**: CLIProxyAPI upstream does not support Qwen; removed from built-in OAuth providers, login flags, token detection, and documentation.
- **Refresh button visibility**: global refresh button now visible with proper icon styling.
- **icon-btn click feedback**: added `:pressed` pseudo-class styling to prevent default Fluent button background flash.
- `/v1/models` health and model fetch requests now include the selected default CLIProxy bearer token when CLIProxy auth is enabled.
- Available models now keep polling after CLIProxy health passes, covering startup races while auth/models are still loading.
- Manual Agents config previews refresh when available models load while the dialog is already open.
- CLIProxy `proxy-config.yaml` API key output no longer corrupts YAML formatting.
- Amp setup now writes `settings.json` plus `secrets.json` using the base URL without `/v1`.
- Codex setup now writes both `config.toml` and `auth.json`.
- Secret input dialogs no longer retain cancelled API keys when reopened.
- Legacy settings migration avoids stripping `Providers` and `PerplexityAccounts` before migration consumers can run.
- Agent detection speed: PATH and well-known directories are scanned first (no subprocess); `where.exe`/`which` is used only as a fallback; binary name candidates are checked in parallel; `$PATH` split is computed once at startup.
- Pi `baseUrl` now correctly includes `/v1` in the generated `models.json`.
- Pi and OpenCode `apiKey` field always written; uses `"no-key"` placeholder when CLIProxy auth is disabled so provider schemas that require a non-empty key do not reject the config.
- Pi provider key in `models.json` renamed from `cliproxy` to `tunnel-agent` for consistency with OpenCode.
- Pi config path corrected to `~/.pi/agent/models.json`.
- All agent config files written as UTF-8 without BOM; prevents JSON parse errors in runtimes that do not strip the BOM.
- `ApplyAgentConfigAsync` finally block dispatched to UI thread; fixes unhandled `InvalidOperationException` (`Call from invalid thread`) when applying agent config.
- Start/stop engine-action button colours restored after `icon-btn` `Foreground` override introduced a style-specificity conflict.
- Quota sidebar badge now counts providers with at least one active account instead of all quota-supported providers.
- Agent configure tooltip translated to English (`"Configurar"` → `"Configure"`, `"Reconfigurar"` → `"Reconfigure"`).

## [0.3.1] - 2026-05-23

### Added

- Supported Ecosystem section in README listing AI providers and a placeholder for future agent integration.
- App Data Storage section in README documenting paths for engine binaries and application settings.
- Auth File Storage section in README documenting paths for CLIProxyAPI OAuth tokens and Perplexity session tokens.
- `SettingsService.LoadSync()` for synchronous settings read at startup to prevent flash-of-wrong-theme before the window is shown.

### Changed

- Brand accent colour updated to `#146CF9` (extracted from the logo) across light and dark themes, replacing the previous `#0A84FF` / `#4DA3FF`.
- Primary button text stays white on hover in light mode; Fluent template no longer overrides foreground on pointer-over.
- Horizontal scroll disabled on the main content area (`HorizontalScrollBarVisibility="Disabled"`); the app layout is fully vertical and horizontal scrolling was never intentional.
- Default window size changed to 820×620; minimum size set to 600×500.
- README hero updated to reference both CLIProxyAPI and Perplexity WebUI Scraper with direct links.
- README badges moved above navigation links.
- Credits rewritten as a concise bullet list; perplexity-webui-scraper added.
- `Build from Source` section renamed to `Development` with a single code block instead of a numbered list.
- Em-dashes replaced with colons, semicolons, and commas throughout README and CONTRIBUTING.
- SECURITY.md updated: supported versions table now shows 0.3.x as the only supported release; reporting instructions link directly to the GitHub private advisory form.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Flash-of-wrong-theme on startup when light mode is saved: settings are now read synchronously before the window is created so `RequestedThemeVariant` is applied before the first render.

### Added

- Perplexity engine controls in the system tray alongside CLIProxyAPI, including status, start, stop, and restart actions.
- Inline token generator in the Add Perplexity Account modal: email → OTP → optional TOTP (three-step wizard, Step 1–3 of 3).
- TOTP (two-factor authentication) support in the token generator flow, compatible with the updated perplexity-webui-scraper fork.
- Email validation before sending OTP request; invalid email shows a contextual styled error inside the modal.
- TOTP code validation (6-digit check) before sending the authenticator code.
- Enter key advances the token wizard steps and saves the account from the manual Session Token form.
- Contextual styled error alert for token generation failures with friendly guidance (2FA / manual cookie fallback).
- Edit label button (pencil icon) on Perplexity session account rows, with a modal dialog and Enter-to-save support.

### Changed

- README now documents the multi-engine architecture, first-class Perplexity support, and engine-specific account storage.
- Providers view server action now uses compact play/stop icon buttons instead of text buttons.
- Copy-endpoint and engine action buttons now use subtler hover/press transitions.
- Add Perplexity Account modal: label field moved above session token field.
- Token generator step labels changed from "Step N of 2" to "Step N of 3" to reflect the optional TOTP third step.
- SlidingTabBar tab text uses FgBrush (theme-aware) for inactive tabs and white for active tab in light mode; white for all tabs in dark mode; no text color change on hover.
- SlidingTabBar background and border now update immediately when switching between light and dark themes.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Returning from Perplexity Configuration to Providers now re-syncs the focused engine with the selected Providers tab.
- Manual install of a pinned engine version no longer immediately auto-installs again because of update detection.
- Explicit "Update now" now installs the latest release instead of reusing the pinned combo-box version.
- Available models fetched from the Perplexity engine no longer show OpenAI branding when Perplexity is the active source.
- Engine action hover artifacts/border seams in the Providers endpoint controls.
- Token generator modal now shows "Step 1 of 3" immediately on first open without requiring a back/forward navigation.
- Token generation errors (including TOTP-related failures) are shown inside the modal instead of the Configuration status area.
- Submitting the TOTP code no longer requires two attempts; stale TOTP prompts no longer re-trigger the input step.
- Token generator correctly handles Rich/ANSI terminal output from the upstream CLI binary.
- Session token is automatically placed in the Session Token field after a successful token generation flow.
- SlidingTabBar no longer stays dark when switching to light mode.
- Active tab text in SlidingTabBar stays white on hover instead of reverting to the inactive color.

## [0.3.0] - 2026-05-21

### Added

- First-class Perplexity engine support alongside CLIProxyAPI, including engine switching from Providers and Configuration.
- Perplexity account management UI with add, remove, default-account selection, and reset confirmation.
- Full-window overlays for account dialogs and confirmation flows so dimming covers the whole app.
- Sliding top tab bars with animated focus indicator for Providers and Configuration.
- GitHub release metadata caching with manual invalidation on refresh/update checks to avoid rate limiting.
- Automatic install-on-startup support for the Perplexity engine when it is missing.
- Sidebar dual-engine status overview for CLIProxyAPI and Perplexity.

### Changed

- Providers page now supports both CLIProxyAPI and Perplexity with focused engine state, per-engine endpoint/start-stop controls, and engine-specific model loading.
- Configuration page reorganized into top tabs: General, CLIProxyAPI, and Perplexity.
- Perplexity security/configuration actions now point to the app settings folder, not the CLIProxy auth folder.
- Perplexity accounts are now stored as separate JSON files under the Tunnel Agent settings directory instead of `settings.json` or `.cli-proxy-api`.
- App startup now defaults Providers focus to CLIProxyAPI while keeping engine selection logic consistent across pages.
- Sidebar branding updated: smaller Tunnel Agent lockup in sidebar, version removed from titlebar.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Available models now query the active engine endpoint/port instead of always using CLIProxyAPI.
- Restored provider-group expansion UI for available models after the multi-engine refactor.
- Perplexity startup health polling no longer crashes the app on `HttpClient` timeout retries.
- Perplexity process startup now surfaces early stderr/crash details instead of only showing a generic timeout.
- Provider account quota bars are rendered again in the expanded account cards.
- Window-root dialogs now dim the full app instead of only the content pane.
- Sidebar status rendering/alignment issues after layout refactor.
- Migration added from legacy `settings.json` Perplexity accounts into file-based account storage.

## [0.2.5] - 2026-05-18

### Added

- Check for update button in Configuration → Engine row, visible when no update is pending. Disables while checking.
- Theme selector in Configuration → General: System, Light, or Dark. Persisted across restarts. Sidebar toggle still cycles between Light and Dark in session.
- Confirmation dialog before resetting credentials, rendered as a window-level overlay so it floats correctly over the full app.
- Default settings are written to `settings.json` when the file is empty or missing fields, so new installs and manual edits always produce a complete settings file.
- 230+ passing unit tests.

### Changed

- Repository restructured into `src/TunnelAgent.Avalonia`, `src/TunnelAgent.Core`, `src/TunnelAgent.Infrastructure`, `src/TunnelAgent.Abstractions`, and `tests/TunnelAgent.Tests`. `TunnelAgent.slnx` at root.
- `IsDark` removed from `AppSettings`; theme preference is now `ThemeMode = system|light|dark`.
- `LogLevel` removed from `AppSettings`; CLIProxyAPI always starts with `debug: false`.
- Credential reset now backs up managed token files to `.tunnelagent-backup/{timestamp}/` before deleting them, and only deletes TunnelAgent-managed files (OAuth prefixes and `openai-compat-*.json`). Unrelated JSON in the auth folder is preserved.
- Status messages from credential operations (reset, open folder) appear in the Configuration view, not the Providers view.
- Reset credentials confirmation and status feedback are shown in the correct UI context.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Loading an empty `settings.json` now correctly writes and applies default values.
- OAuth `ConnectAsync` test for a known provider no longer launches the real CLIProxyAPI binary during unit test runs.
- `LaunchAtLoginService` no longer references the `Program` type from the Avalonia project when running from Infrastructure.

### Security

- Pinned `Tmds.DBus.Protocol` to `0.21.3` to resolve CVE-2026-39959 (high severity).

## [0.2.4] - 2026-05-18

### Added

- Routing strategy option in Configuration: Round Robin (even distribution) or Fill First (use first account until limit). Applies to both API keys and OAuth accounts.
- "Report an issue" link in sidebar footer opens the GitHub issues page.
- Test suite with 75 passing tests covering converters, engine config, OAuth token detection, provider catalog, auth file watcher, settings, credentials, view models, and service smoke tests.

### Changed

- Auto-update toggle is now disabled when auto-check for updates is off, preventing an inconsistent configuration.
- ComboBox controls show a hand cursor on hover to indicate they are selectable.
- Agents section is disabled in the UI until fully implemented.

## [0.2.3] - 2026-05-17

### Added

- Launch at login now works across Windows, macOS, and Linux.
- System tray controls let users show or hide the window, manage CLIProxyAPI, open configuration, open the auth folder, and quit the app.

### Changed

- The desktop app now targets cross-platform .NET instead of a Windows-only target framework.
- Closing the main window hides the app to the tray; quitting from the tray exits the app and stops the server.

## [0.2.2] - 2026-05-17

### Added

- Configuration now includes richer CLIProxyAPI release selection and install controls.

### Changed

- Provider state and server startup behavior are more reliable during initialization.
- Removed the unused Activity section from the desktop navigation.

## [0.2.1] - 2026-05-14

### Added

- Provider management now supports connected accounts, enable/disable state, OAuth and custom providers, live quota bars, and available models from the running proxy.
- Provider rows were redesigned with clearer connection status, account panels, refresh actions, and provider icons.
- OAuth callback pages now show branded success and error states.

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Provider account detection, quota display, refresh animations, add-account tooltips, and account enable/disable persistence now behave correctly.
- CLIProxyAPI owns the OAuth callback port directly, avoiding unsupported callback interception in the desktop app.

## [0.2.0] - 2026-05-14

### Added

#### Provider Catalog

- `ProviderCatalogService` — orchestrates the full provider lifecycle: auth-dir sync, add/remove accounts, enable/disable, config.yaml rewrite
- `OAuthTokenDetector` — reads `~/.cli-proxy-api/{prefix}-{email}*.json` files to detect connected OAuth providers and parse account metadata (email, plan badge)
- `AuthFileWatcher` — `FileSystemWatcher` with 400 ms debounce over `~/.cli-proxy-api/` to react to new/removed token files in real time
- `CustomProviderCredentialStore` — reads/writes `openai-compat-{id}-{uuid}.json` credential files, compatible with CLIProxyAPI format
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

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

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

## [0.1.0] - 2026-05-10

### Added

- Initial Avalonia UI with custom titlebar, collapsible sidebar, and dark/light theme toggle
- Providers view with connected service cards and sparkline usage graphs
- Agents view with install status and route provider assignment
- Activity view with request log (method, path, agent, provider, model, status, latency)
- Configuration view with General, Network, Security, and Engine sections
- `EngineDownloadService` — downloads and installs the CLIProxyAPI binary from GitHub Releases
  - Platform-aware asset selection (Windows zip, macOS/Linux tar.gz)
  - Progress reporting during download
  - Automatic first-launch download when binary is missing
- `EngineConfigService` — generates `proxy-config.yaml` in the platform settings directory
  - Windows: `%AppData%\TunnelAgent\proxy-config.yaml`
  - macOS: `~/Library/Preferences/TunnelAgent/proxy-config.yaml`
  - Linux: `~/.config/TunnelAgent/proxy-config.yaml`
- `EngineProcessService` — starts and monitors the CLIProxyAPI process
  - Launches with `-config <path>` flag
  - Health polling to confirm server is up before marking Running
  - Crash detection via process Exited event
- `EngineService` — thin orchestrator composing the three services above
- `IPlatformInfo` interface with `WindowsPlatform`, `MacOsPlatform`, `LinuxPlatform` implementations
  - Per-platform binary name, archive format, OS/arch suffix, settings/data/auth directories
- `AppSettings` and `SettingsService` — persistent settings in platform roaming directory
- Update check via GitHub redirect URL (no API rate limit)
- Auto-check for updates on startup (configurable)
- Auto-update toggle (downloads and installs silently)
- Update notification: sidebar badge, startup toast (auto-dismisses after 8s), inline ENGINE card
- Manual "Update now" button with progress bar and success banner
- Port change in UI immediately rewrites config and restarts the engine if running
- Management control panel download disabled (`disable-control-panel: true`)

### Added

- **Pi context window**: when configuring Pi, `contextWindow` is now fetched from OpenRouter's public `/v1/models` endpoint and written per-model in `models.json`. Results are cached in-memory. If a model is not found, the field is omitted (Pi defaults to 128k).

### Fixed

- Binary stored in `%LocalAppData%` (not roaming) — executables should not roam
- Settings stored in roaming `%AppData%` / `~/Library/Preferences` / `~/.config`
- CLIProxyAPI binary keeps its original name (`cli-proxy-api` / `cli-proxy-api.exe`)
- Version parsing handles `CLIProxyAPI Version: 7.0.2, Commit: ...` output format
- Version comparison normalises `v` prefix to avoid false update notifications
- Update check uses GitHub HTML redirect instead of API endpoint to avoid 60 req/h rate limit
- Engine always reads version from binary at startup (never trusts cached value)
- Update notification triggers reactively from `StateChanged` rather than at a fixed startup point

[Unreleased]: https://github.com/Villoh/tunnel-agent/compare/v0.5.5...HEAD
[0.5.5]: https://github.com/Villoh/tunnel-agent/compare/v0.5.4...v0.5.5
[0.5.4]: https://github.com/Villoh/tunnel-agent/compare/v0.5.3...v0.5.4
[0.5.3]: https://github.com/Villoh/tunnel-agent/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/Villoh/tunnel-agent/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/Villoh/tunnel-agent/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/Villoh/tunnel-agent/compare/v0.4.5...v0.5.0
[0.4.5]: https://github.com/Villoh/tunnel-agent/compare/v0.4.4...v0.4.5
[0.4.4]: https://github.com/Villoh/tunnel-agent/compare/v0.4.3...v0.4.4
[0.4.3]: https://github.com/Villoh/tunnel-agent/compare/v0.4.2...v0.4.3
[0.4.2]: https://github.com/Villoh/tunnel-agent/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/Villoh/tunnel-agent/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Villoh/tunnel-agent/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/Villoh/tunnel-agent/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/Villoh/tunnel-agent/compare/v0.2.5...v0.3.0
[0.2.5]: https://github.com/Villoh/tunnel-agent/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/Villoh/tunnel-agent/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/Villoh/tunnel-agent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/Villoh/tunnel-agent/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Villoh/tunnel-agent/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Villoh/tunnel-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Villoh/tunnel-agent/releases/tag/v0.1.0
