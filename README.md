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

- **.NET 11 SDK** or runtime (macOS, Linux, Windows)

### Installing as a Global .NET Tool

```bash
# Pack and install locally
dotnet pack Limits.Cli/Limits.Cli.fsproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg limits-cli
```

### Building & Running from Source

```bash
# Clone the repository
git clone https://github.com/euxaristia/limits.git
cd limits

# Run CLI status check
dotnet run --project Limits.Cli/Limits.Cli.fsproj -- status

# Run CLI with JSON output
dotnet run --project Limits.Cli/Limits.Cli.fsproj -- --json
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
├── Limits.Core/         # Shared cross-platform F# core library (Models, Fetchers, ConfigStore)
├── Limits.Cli/          # Cross-platform CLI executable (`limits`)
├── Tests/               # Unit test suite (xUnit)
└── Limits.slnx          # .NET Solution file
```

---

## 🖥️ Desktop App

Looking for the Windows Desktop / System Tray app?
See the companion private repository: [**`limits-windows`**](https://github.com/euxaristia/limits-windows) (WinUI 3 tray application with Mica backdrop popover).

---

## 📄 License

MIT License.
