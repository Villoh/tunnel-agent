# Plan: Skills window (powered by `asm` / agent-skill-manager)

> Status: **DRAFT — ready to pick up**. This document is written so another
> agent/session can implement it end‑to‑end. Distribution strategy is decided:
> use the user's compatible Node/npm installation and install `asm` locally under
> TunnelAgent's data directory. No fork or custom binary packaging.

## 1. Goal

Add a new **"Skills"** section/window to TunnelAgent (Avalonia app) that lets the
user browse, search, inspect, install, update and remove *agent skills* for the
**Pi** agent (`~/.pi/agent/skills/`, `.pi/skills/`).

We do **not** reinvent skill discovery/installation. We drive the
**`asm` (agent-skill-manager)** CLI as the engine. `asm`:

- Supports Pi as a first‑class provider (`-p pi`) mapping to the correct paths.
- Emits machine‑readable JSON on every relevant command (`--json`) and is
  non‑interactive with `--yes`.
- Has a curated catalog (~4.3k skills), local security audit, bundles.
- MIT, no accounts, no telemetry.

`asm` was chosen over `skills.sh` because skills.sh only emits JSON on `list`
(everything else is ANSI text), has telemetry, and its rich HTTP API is gated
behind Vercel OIDC auth (unusable from a distributed desktop client without us
hosting a proxy). See conversation history for the full comparison.

## 2. Distribution strategy (DECIDED)

**`asm` is distributed via npm only.** `package.json` declares
`bin: { asm, agent-skill-manager } -> dist/agent-skill-manager.js`, requires
`node >=18 <23`, and npm `>=9`. GitHub releases do not provide native
single-file binaries.

TunnelAgent will **not** maintain a fork, packaging repository, Node runtime, or
global npm installation. It will:

1. Detect `node` and `npm` from the user's `PATH` when the Skills section opens.
2. Validate Node `>=18 <23` and npm `>=9`.
3. If missing or incompatible, show the exact requirement, a link to the Node.js
   download page, and a **Recheck** action. Skills commands remain disabled.
4. If compatible but `asm` is absent, offer **Install ASM**.
5. Install a pinned `agent-skill-manager` version into:
   `<LocalDataDirectory>/skills-engine` using npm `--prefix`.
6. Invoke the locally installed package through the detected Node executable;
   never use or modify a global `asm` installation.
7. Check the npm registry for newer versions and update only after explicit user
   action.

Commands:

```bash
npm install --prefix <skills-engine> agent-skill-manager@<version> --save-exact
npm view agent-skill-manager version
```

Local entry point:

`<skills-engine>/node_modules/agent-skill-manager/dist/agent-skill-manager.js`

Invoke it with the detected Node executable. This avoids platform-specific npm
shims (`asm.cmd` vs `asm`) and shell execution.

`package.json` and `package-lock.json` become installation metadata. npm handles
package integrity; no custom `install.json`, GitHub release cache, archive
extraction, SHA256 manifest, or custom binary release pipeline.

## 3. Architecture — how it maps onto the existing codebase

`asm` is a **CLI invoked per command**, NOT a long‑running server with a port.
Therefore **do not force it into `IManagedEngine`** (that interface assumes
`Port`, `StartAsync/StopAsync`, `IsRunning`). Instead:

- **Add** a small `AsmProvisionService` that detects Node/npm, validates versions,
  installs `asm` locally with npm, and checks the registry for updates.
- **Add** an `AsmCliService` that invokes the local package through Node and
  parses JSON.
- **Add** a `SkillsViewModel` + `SkillsView` for the UI, wired into navigation
  like `Agents`.

### New namespace

```text
src/TunnelAgent.Infrastructure/Skills/
    AsmProvisionService.cs  # detect Node/npm; provision local npm package
    AsmCliService.cs        # invoke local package with Node; parse JSON
src/TunnelAgent.Core/Skills/
    SkillModels.cs          # skill, audit, and command DTOs
src/TunnelAgent.Avalonia/ViewModels/
    SkillsViewModel.cs
    SkillViewModel.cs       # per-skill row VM
src/TunnelAgent.Avalonia/Views/
    SkillsView.axaml (+ .axaml.cs)
```

Reuse existing infra directly: `IPlatformInfo.LocalDataDirectory`,
`SettingsService`, process execution patterns, `IFolderOpenService`, and existing
status/toast conventions. Archive, chmod, and GitHub-release helpers are not
needed.

## 4. Component breakdown

### 4.1 `AsmProvisionService` (Infrastructure/Skills)

Small wrapper around system Node/npm and the local npm prefix:

- `InstallDirectory = Path.Combine(Platform.LocalDataDirectory, "skills-engine")`.
- `EntryPointPath` resolves to
  `node_modules/agent-skill-manager/dist/agent-skill-manager.js`.
- Preserve the detected Node executable path and use it to run the entry point.
  Resolve npm separately; on Windows this may be `npm.cmd`, requiring the
  platform-safe command-launch pattern already used by the codebase.
- `CheckPrerequisitesAsync(ct)` runs `node --version` and `npm --version`, then
  returns structured status: found paths, versions, compatibility, and a
  user-facing failure reason.
- Validate Node `>=18 <23` and npm `>=9`; command existence alone is insufficient.
- `IsAsmInstalled()` checks the local entry point and package metadata. Ignore
  global `asm` installations to keep behavior deterministic.
- `GetInstalledVersionAsync(ct)` reads the exact local package version via npm or
  `node_modules/agent-skill-manager/package.json`; prefer direct JSON read.
- `CheckForUpdateAsync(ct)` runs
  `npm view agent-skill-manager version --json` only after prerequisites pass.
- `InstallAsync(version, ct)` runs
  `npm install --prefix <dir> agent-skill-manager@<version> --save-exact`.
- `UpdateAsync(version, ct)` uses the same install command. One code path is
  enough for install and update.
- Capture stdout/stderr, cancellation, and non-zero exit codes. Expose simple
  states: CheckingPrerequisites, NotAvailable, NotInstalled, Installing, Ready,
  UpdateAvailable, Error.
- Serialize install/update operations with one `SemaphoreSlim`; npm must not run
  twice against the same prefix.
- Do not add an interface unless tests require a seam already used by neighboring
  services; one implementation does not justify abstraction.

### 4.2 `AsmCliService` (Infrastructure/Skills)

Thin wrapper around `System.Diagnostics.Process` that runs the local JavaScript
entry point with detected Node and deserializes JSON. All calls pass `--json`
and, for mutations, `--yes`.

Proposed API (async, `CancellationToken`):

```csharp
Task<IReadOnlyList<SkillSummary>> ListInstalledAsync(ct);              // asm list -p pi --json
Task<IReadOnlyList<SkillSummary>> SearchAsync(string q, ct);          // asm search "<q>" --json
Task<SkillDetail> InspectAsync(string skillId, ct);                   // asm inspect <skill> -p pi --json
Task<SkillAudit>  AuditSecurityAsync(string source, ct);             // asm audit security <src> --json
Task<InstallResult> InstallAsync(string source, ct);                 // asm install <src> -p pi --yes --json
Task<CommandResult> UninstallAsync(string skill, ct);                // asm uninstall/remove <skill> -p pi --yes --json
Task<CommandResult> AuditDedupeAsync(ct);                            // asm audit --yes --json
Task<CommandResult> BundleInstallAsync(string bundle, ct);           // asm bundle install <name> -p pi --yes --json
string? BinaryVersion();                                             // asm --version
```

Implementation notes:

- Central `RunAsync(args, ct)` helper: `ProcessStartInfo` with
  `RedirectStandardOutput/Error`, `UseShellExecute=false`, `CreateNoWindow=true`,
  `ArgumentList` (never string concat → avoids injection). Read stdout+stderr,
  await exit, throw a typed `AsmCliException` on non‑zero exit including stderr.
- `System.Text.Json` with case-insensitive property names.
- Model the DTOs against the **real** JSON captured in Phase 0 (do not guess).
- Consider a short timeout for `search`/`inspect` (network) and a longer one for
  `install`.

### 4.3 DTOs (`Core/Skills/SkillModels.cs`)

Define after Phase 0 confirms shapes. Expected fields (verify!):
`name`, `description`, `source`/`repo`, `installed` (bool), `version`,
`path`, plus audit: `status` (pass/warn/fail), `riskLevel`, `summary`,
`categories`.

### 4.4 Settings additions (`Core/Models/AppSettings.cs`)

Add a small `SkillsSettings` block (persisted via `SettingsService`):

```csharp
public sealed class SkillsSettings
{
    public bool AutoCheckForUpdates { get; set; } = true;
    public string PreferredVersion { get; set; } = "2.14.0";
    public string Scope { get; set; } = "global";
}
```

Add `public SkillsSettings Skills { get; set; } = new();` to `AppSettings`.
Reuse the global `AutoCheckForUpdates` toggle if preferred, but a dedicated one
avoids coupling with engine updates.

### 4.5 Composition and lazy initialization (`App.axaml.cs` + ViewModels)

- Construct `AsmProvisionService` + `AsmCliService` in `App.axaml.cs` alongside
  other services and pass them to `SkillsViewModel`.
- Do **nothing** for Skills during application startup. No Node checks, registry
  requests, npm installs, or skill commands before the user opens the section.
- On first navigation to Skills:
  1. Run `CheckPrerequisitesAsync`.
  2. If Node/npm is missing or incompatible, show the prerequisite message and
     stop. Provide **Open Node.js download page** and **Recheck** actions.
  3. If prerequisites pass but local `asm` is absent, show **Install ASM**. Do not
     install automatically.
  4. If local `asm` exists, load installed skills and, when enabled, check for an
     update in the background.
- Re-entering the section reuses loaded state. **Recheck** explicitly repeats
  prerequisite detection after the user installs or upgrades Node.

### 4.6 Navigation

1. `src/TunnelAgent.Core/Models/Enums.cs`: add `Skills` to `SectionKey`.
2. `MainWindow.axaml`: add a sidebar `<Button Classes="side" ...
   ConverterParameter=Skills Command="{Binding SelectSkillsCommand}">` with a
   lucide icon (e.g. `Kind="Sparkles"` or `Puzzle`) — copy the Agents button
   block (~line 238) including the count badge if desired
   (`InstalledSkillCount`).
3. `MainWindow.axaml.cs`: add `SectionKey.Skills => new SkillsView()` to
   `CreateSectionView`.
4. `MainWindowViewModel`: add `[RelayCommand] private void SelectSkills() =>
   SelectedSection = SectionKey.Skills;` and lazy-load hook in
   `OnSelectedSectionChanged` (`if (value == SectionKey.Skills && !_skillsLoadedOnce) _ = LoadSkillsAsync();`).
5. Add localization key `Sidebar_Skills` (+ any UI strings) to every
   `Resources/*.resx`/localization source used by `{l:Loc ...}`.

### 4.7 ViewModels

`SkillsViewModel` (can live inside MainWindowViewModel like `LogsViewModel`/
`DashboardViewModel`, or as a standalone VM exposed as a property). Responsible
for:

- `ObservableCollection<SkillViewModel> Installed`, `SearchResults`.
- Commands: `Search`, `Install(skill)`, `Uninstall(skill)`, `Inspect(skill)`
  (shows SKILL.md + file tree), `RunSecurityAudit(skill)`, `Refresh`,
  `OpenSkillsFolder` (reuse `IFolderOpenService`).
- Provisioning state surface: detected Node/npm versions,
  `PrerequisitesAvailable`, `PrerequisiteMessage`, `AsmInstalledVersion`,
  `AsmUpdateAvailable`, `IsAsmReady`, plus `RecheckPrerequisites`, `InstallAsm`,
  and `UpdateAsm` commands backed by `AsmProvisionService`.
- Busy/error toasts consistent with existing patterns
  (`ShowConfigurationStatus`, auto‑dismiss CTS pattern).
- **Security gate:** before `Install`, call `AuditSecurityAsync` and show the
  result (status + risk + summary) in a confirm dialog. Never install silently.

`SkillViewModel`: name, description, source, installed?, version, audit badge,
per-row commands.

### 4.8 View (`SkillsView.axaml`)

Follow the visual language of `AgentsView`/`ProvidersView`. Sections:

- Prerequisite state: when Node/npm is missing or incompatible, show
  "Skills requires Node.js 18–22 and npm 9 or newer", detected versions when
  available, **Open Node.js download page**, and **Recheck**. Hide/disable all
  skill operations.
- ASM state: when prerequisites pass but local `asm` is absent, explain that it
  will be installed only inside TunnelAgent data, then show **Install ASM**.
- Ready state: installed version, optional update notice, **Update ASM**, search
  bar, and results list.
- Installed skills list (uninstall, re-audit, open folder).
- Detail pane / dialog: SKILL.md preview + file tree + full audit report.
- Empty states (binary not installed yet / no skills / offline).

## 5. `asm` CLI contract used by the UI

| UI action | Command (verify exact flags in Phase 0) |
| ----------- | ------------------------------------------ |
| List installed (Pi) | `asm list -p pi --json` |
| Search catalog | `asm search "<query>" --json` |
| Skill detail | `asm inspect <skill> --json` |
| Security scan | `asm audit security <source> --json` |
| Install to Pi | `asm install <source> -p pi --yes --json` |
| Uninstall | `asm uninstall <skill> -p pi --yes --json` (confirm verb: `uninstall` vs `remove`) |
| Remove duplicates | `asm audit --yes --json` |
| Install bundle | `asm bundle install <name> -p pi --yes --json` |
| Binary version | `asm --version` |

Provider id for Pi in `asm` is `pi`. Confirm scope flags (global vs project) map
to `~/.pi/agent/skills` vs `.pi/skills`.

## 6. Phase 0 — verification (DO THIS BEFORE CODING)

Non‑negotiable: capture real behavior so DTOs/commands aren't guessed.

1. Validate provisioning in a temporary prefix:
   `npm install --prefix <temp>/skills-engine agent-skill-manager@2.14.0 --save-exact`.
2. Invoke the local shim and **save raw JSON** for:
   `asm list -p pi --json`,
   `asm search "code review" --json`, `asm inspect <some-skill> --json`,
   `asm audit security github:anthropics/skills --json`,
   `asm install <skill> -p pi --yes --json` (dry run if available),
   `asm --version`, `asm --help`.
3. Confirm exact Pi provider id, scope flags, and uninstall verb.
4. Confirm exit codes on error and where errors are printed (stdout vs stderr).
5. Confirm local package entry point and Node-based invocation on Windows,
   Linux, and macOS.
6. Capture outputs for missing Node, incompatible Node, missing npm, failed npm
   install, offline registry, and `npm view agent-skill-manager version --json`.

Store captured JSON under `docs/plans/asm-samples/` for the implementer.

## 7. Implementation phases

1. **Phase 0** — verification above. Output: sample JSON + confirmed contract.
2. **Phase 1 — DTOs + AsmCliService** (Infrastructure/Skills, Core/Skills) with
   unit tests using captured JSON fixtures. No UI yet.
3. **Phase 2 — Settings + navigation shell**: `SkillsSettings`, `SectionKey.Skills`,
   sidebar button, empty `SkillsView`, `SelectSkillsCommand`. App compiles and
   navigates.
4. **Phase 3 — AsmProvisionService**: Node/npm detection and version validation,
   local pinned npm install/update, lazy section initialization, and prerequisite/
   ASM status UI.
5. **Phase 4 — Skills UI**: search, list installed, install (with mandatory
   security audit confirm), uninstall, inspect/detail, open folder, toasts,
   empty/error states.
6. **Phase 5 — polish**: localization for all strings, bundles, dedupe audit,
   version pinning UI, theming parity, accessibility.

## 8. Testing strategy

Mirror existing tests in `tests/TunnelAgent.Tests/`:

- `AsmProvisionServiceTests` — Node/npm missing and incompatible versions, local
  entry-point resolution, package version parsing, install/update command construction,
  cancellation, and non-zero npm exit.
- `AsmCliServiceTests` — feed captured JSON fixtures into the parser; assert DTO
  mapping and error handling on non‑zero exit / malformed JSON.
- `SkillsViewModelTests` — command behavior, audit-before-install gate, busy/error
  state (see `PerplexityEngineServiceTests`, `MainWindowViewModelTests`).
- `SettingsServiceTests` — round‑trip `SkillsSettings`.

## 9. Security considerations

- Skills can instruct the agent to run arbitrary code and may bundle scripts.
  **Always** surface `asm audit security` results and require explicit user
  confirmation before install. Show source, risk level, summary, categories.
- Build all process invocations with `ArgumentList` (no shell string concat).
- Install only the pinned package/version from the official npm registry. Use
  npm's lockfile and integrity verification; never execute shell-composed input.
- Never install Node, `asm`, updates, or skills automatically. Registry update
  checks may run after the user opens Skills when enabled.
- Keep npm prefix inside `LocalDataDirectory`; never require administrator rights
  or mutate global npm packages.

## 10. Open decisions (need user/owner input)

1. Default scope: global (`~/.pi/agent/skills`) vs project (`.pi/skills`)?
   Recommend global default with a scope toggle.
2. Separate window vs sidebar section? This plan assumes a **sidebar section**
   consistent with Agents/Providers.
3. Keep dedicated `Skills.AutoCheckForUpdates` or reuse the global setting?
   Plan assumes dedicated.

Decided: system Node/npm prerequisite, local npm prefix, pinned ASM version,
manual install/update, no fork, no custom binary releases.

## 11. Reference files (study before implementing)

- Process execution/error patterns in existing infrastructure services.
- Engine contract (for reference, NOT to implement): `Core/Engine/IManagedEngine.cs`.
- Local data directory: `Infrastructure/Services/IPlatformInfo.cs`.
- Settings model + `GetOrAddEngine`: `Core/Models/AppSettings.cs`,
  `Infrastructure/Services/SettingsService.cs`
- App composition root: `Avalonia/App.axaml.cs`
- Navigation + section hosting: `Core/Models/Enums.cs` (`SectionKey`),
  `Avalonia/Views/MainWindow.axaml` (sidebar buttons ~lines 155‑300 +
  `SectionHost` ~line 444), `Avalonia/Views/MainWindow.axaml.cs`
  (`CreateSectionView`)
- ViewModel patterns (commands, toasts, lazy section load):
  `Avalonia/ViewModels/MainWindowViewModel.cs`
- View patterns: `Avalonia/Views/AgentsView.axaml`, `ProvidersView.axaml`
- Folder open helper: `Abstractions/Services/IFolderOpenService.cs`

## 12. `asm` facts (as of v2.14.0)

- npm pkg `agent-skill-manager`, bins `asm` + `agent-skill-manager`.
- `engines: node >=18 <23`, `npm >=9`. Runtime deps pure JS (ink/react/yaml).
- `postinstall.cjs` runs during local npm installation and prepares the catalog
  index; standard npm installation handles it.
- Package ships catalog data under `data/`; no separate packaging work needed.
- Pi provider maps to `~/.pi/agent/skills` (global) and `.pi/skills` (project).
- Repo: <https://github.com/luongnv89/asm>
