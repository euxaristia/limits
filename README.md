# Limits 🎚️

Every AI coding limit & quota, right in your terminal and system tray.

**Limits** is a cross-platform monitoring suite for AI model quotas, API limits, and token usage (OpenAI, Claude, Gemini, Antigravity, Cursor, DeepSeek, OpenRouter, ElevenLabs, Groq, AWS Bedrock).

It consists of:
1. **`limits` CLI** (Cross-platform — macOS, Linux, Windows): A lightweight command-line interface for viewing quotas, managing configuration, and exporting raw JSON for bar applets (Waybar, SketchyBar, Polybar, Rofi, tmux).
2. **`Limits.Core`** (Cross-platform F# Library): Shared engine for domain models, JSON configuration, cross-platform credential resolution, and parallel API fetchers.
3. **`Limits` GUI Status Bar** (Windows): A native Windows App SDK / WinUI 3 system tray application with Mica backdrop popovers.

---

## Features

- **Cross-Platform CLI**: Interactive terminal output with progress bars, status indicators, and ANSI color formatting.
- **JSON Output Mode**: Pass `--json` to pipe structured metrics to status bars, scripts, or command launchers on macOS, Linux, or Windows.
- **Native Windows System Tray GUI**: Lives quietly in your Notification Area via `H.NotifyIcon` with a borderless `MicaBackdrop` popover.
- **Shared Configuration**: Shared config format at `~/.config/limits/config.json` (with automatic fallback to `~/.config/codexbar/config.json` and `LIMITS_CONFIG` env var overrides).
- **Parallel Provider Fetching**: Concurrent API usage requests for fast response times.

---

## Project Architecture

```
Limits.slnx
├── Limits.Core/         # Shared cross-platform F# core library (Models, Fetchers, ConfigStore)
├── Limits.Cli/          # Cross-platform CLI executable (`limits`)
├── Limits/              # Windows App SDK / WinUI 3 tray app
└── Tests/               # Unit tests (xUnit)
```

---

## CLI Usage (`limits`)

### Build & Run CLI

```bash
# Run CLI status check
dotnet run --project Limits.Cli/Limits.Cli.fsproj -- status

# Run CLI with JSON output
dotnet run --project Limits.Cli/Limits.Cli.fsproj -- --json

# Filter to a specific provider
dotnet run --project Limits.Cli/Limits.Cli.fsproj -- -p claude
```

### CLI Commands & Flags

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

---

## Windows Status Bar GUI

### Build & Run GUI App

```powershell
dotnet run --project Limits.csproj
```

---

## Requirements

- **CLI (`limits`)**: .NET 11 SDK or runtime (macOS, Linux, Windows)
- **GUI App**: Windows 10/11 (x64 / ARM64) with .NET 11 SDK or later
