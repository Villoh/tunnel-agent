# Contributing to Tunnel Agent

## Getting Started

1. Fork the repository and create a branch from `main`.
2. Install [.NET SDK 10.0.203+](https://dotnet.microsoft.com/download).
3. Restore dependencies and run the app:
   ```pwsh
   dotnet restore
   dotnet run
   ```

## Pull Requests

- Keep PRs focused: one feature or fix per PR.
- Match existing code style and naming conventions.
- Update the README if your change affects documented behaviour.
- All CI checks must pass before a PR can be merged.
- Add a bullet in `CHANGELOG.md` under `## [Unreleased]` for every behavioural change:
  - `### Added` — new feature
  - `### Changed` — modified behaviour
  - `### Fixed` — bug fix
  - `### Removed` — removed feature

## Releases

Releases are published by pushing a semver tag:

```bash
# Stable
git tag v0.5.0 && git push origin v0.5.0

# Pre-release / RC
git tag v0.5.0-rc.1 && git push origin v0.5.0-rc.1
```

Or trigger manually from GitHub → Actions → Release → Run workflow.

The release workflow bumps `.csproj` versions, runs tests, builds a self-contained win-x64 binary, packages it with Velopack (installer + auto-update assets), and creates the GitHub Release automatically.

## Reporting Bugs

Open a [GitHub Issue](../../issues) with:

- OS version and .NET SDK version
- Steps to reproduce
- Expected vs. actual behaviour
- Logs or screenshots if applicable

## Code of Conduct

Be respectful. Constructive criticism only. No harassment or discrimination of any kind.
