# App Data Storage

Tunnel Agent writes non-credential app data to the following locations.

## Engine binaries

| Platform | Path                                                |
| -------- | --------------------------------------------------- |
| Windows  | `%LocalAppData%\TunnelAgent\engine\`                |
| macOS    | `~/Library/Application Support/TunnelAgent/engine/` |
| Linux    | `~/.local/share/TunnelAgent/engine/`                |

## Application settings

| Platform | Path                                              |
| -------- | ------------------------------------------------- |
| Windows  | `%AppData%\TunnelAgent\settings.json`             |
| macOS    | `~/Library/Preferences/TunnelAgent/settings.json` |
| Linux    | `~/.config/TunnelAgent/settings.json`             |
