# limits 🎚️

Every AI coding limit & quota, right in your terminal.

**`limits`** is a fast, cross-platform command-line interface and monitoring engine for AI model quotas, API limits, and token usage (OpenAI, Claude, Gemini, Antigravity, Cursor, DeepSeek, OpenRouter, ElevenLabs, Groq, AWS Bedrock).

Designed to be run as an interactive terminal CLI or piped directly into status bars (Waybar, SketchyBar, Polybar, Rofi, tmux, etc.) via raw JSON output mode.

---

## ✨ Features

- **Cross-Platform CLI**: Interactive terminal output with progress bars, status indicators, and ANSI color formatting (macOS, Linux, Windows).
- **JSON Output Mode**: Pass `--json` to pipe structured metrics directly to status bar scripts, custom widgets, or command launchers.
- **Single-Provider Filtering**: Filter output to specific providers using `-p` / `--provider` (e.g. `-p claude`, `-p antigravity`).
- **Parallel Provider Fetching**: Concurrent API usage requests for rapid response times.
- **Unified Configuration**: Shared config format stored at `~/.config/limits/config.json` (with automatic fallback to `~/.config/codexbar/config.json` and `LIMITS_CONFIG` environment variable overrides).
- **Cross-Platform Credential Resolution**: Automatically detects tokens & credentials from local CLI environments (e.g. `gcloud`, OAuth tokens, environment variables).

---

## 💻 Installation & Requirements

### Requirements

- **Go 1.26+** (macOS, Linux, Windows)

### Installing Global Binary

```bash
# Build and install to $GOPATH/bin or /usr/local/bin
go install github.com/euxaristia/limits@latest
```

### Building & Running from Source

```bash
# Clone the repository
git clone https://github.com/euxaristia/limits.git
cd limits

# Build statically linked binary
go build -ldflags="-s -w" -o limits .

# Run CLI status check
./limits status

# Run CLI with JSON output
./limits --json
```

---

## 🛠️ CLI Usage & Commands

```text
USAGE:
  limits [command] [options]

COMMANDS:
  status (default)        Fetch and display current usage for all active providers
  providers               List all available providers and their enabled state
  config path             Display active config file path
  config show             Display active configuration JSON
  config enable <id>      Enable a provider by ID
  config disable <id>     Disable a provider by ID
  config set-key <id> <k> Set API key for a provider
  help, -h, --help        Show help message
  version, -v, --version  Show version

OPTIONS:
  --json                  Output raw JSON format
  -p, --provider <id>     Filter status to a single provider (e.g. -p antigravity)
  --no-color              Disable colored terminal output
```

### Example Commands

```bash
# View all enabled AI provider status in formatted terminal table
limits status

# Get JSON payload for Waybar or tmux status bar
limits --json

# Check Antigravity quota specifically
limits status -p antigravity

# Enable or disable a provider
limits config enable claude
limits config disable openrouter
```

---

## 🏗️ Architecture

```
limits/
├── main.go              # CLI entry point, argument parsing, output rendering
├── pkg/
│   ├── config/           # Config file loading, defaults, provider enable/disable
│   ├── credentials/      # Cross-platform credential & keyring resolution
│   ├── fetchers/         # Per-provider usage fetching
│   ├── models/           # Shared types (UsageProvider, ProviderUsage, ...)
│   ├── parsers/          # Response parsing per provider (with unit tests)
│   └── terminal/         # ANSI color & progress bar rendering
└── go.mod
```

---

## 🖥️ Desktop App

Looking for the Windows Desktop / System Tray app?
See the companion private repository: [**`limits-windows`**](https://github.com/euxaristia/limits-windows) (WinUI 3 tray application with Mica backdrop popover).

---

## 📄 License

MIT License.
