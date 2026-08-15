# App Data Storage

Tunnel Agent writes non-credential app data to the following locations.

## Engine binaries

| Platform | Path                                                |
| -------- | --------------------------------------------------- |
| Windows  | `%LocalAppData%\TunnelAgent\engine\`                |
| macOS    | `~/Library/Application Support/TunnelAgent/engine/` |
| Linux    | `~/.local/share/TunnelAgent/engine/`                |

CLIProxyAPI and Perplexity binaries live in that `engine/` directory under their engine id. 9Router is installed from the npm tarball into `{LocalDataDirectory}/engine/9router/` (Windows `%LocalAppData%\TunnelAgent\engine\9router\`, macOS `~/Library/Application Support/TunnelAgent/engine/9router/`, Linux `~/.local/share/TunnelAgent/engine/9router/`).

9Router’s own runtime data (SQLite, dashboard session, and so on) stays in 9Router’s directories, not under Tunnel Agent: `%APPDATA%\9router` on Windows and `~/.9router` on macOS/Linux.

## Application settings

| Platform | Path                                              |
| -------- | ------------------------------------------------- |
| Windows  | `%AppData%\TunnelAgent\settings.json`             |
| macOS    | `~/Library/Preferences/TunnelAgent/settings.json` |
| Linux    | `~/.config/TunnelAgent/settings.json`             |
