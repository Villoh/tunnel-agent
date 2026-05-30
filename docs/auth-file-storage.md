# Auth File Storage

Tunnel Agent stores credentials and session data in the following locations.

## CLIProxyAPI OAuth tokens and custom keys

Managed by CLIProxyAPI. Tunnel Agent reads and modifies files in:

| Platform | Path                            |
| -------- | ------------------------------- |
| Windows  | `%UserProfile%\.cli-proxy-api\` |
| macOS    | `~/.cli-proxy-api/`             |
| Linux    | `~/.cli-proxy-api/`             |

The credential reset action backs up and removes only TunnelAgent-managed files (`openai-compat-*.json`, OAuth tokens). Unrelated files in the folder are preserved.

## Perplexity WebUI session tokens

Each Perplexity account is stored as a separate JSON file:

| Platform | Path                                                              |
| -------- | ----------------------------------------------------------------- |
| Windows  | `%AppData%\TunnelAgent\perplexity-accounts\{id}.json`             |
| macOS    | `~/Library/Preferences/TunnelAgent/perplexity-accounts/{id}.json` |
| Linux    | `~/.config/TunnelAgent/perplexity-accounts/{id}.json`             |

Session tokens are stored in plain text; access is restricted to the current user by OS file permissions. Reset backs up files to `.backup/{timestamp}/` before deleting.
