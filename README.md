# limits 🎚️

Every AI coding limit and quota, in one place.

**`limits`** reads how much of each AI subscription is left — Claude, Codex, Antigravity, Grok, GitHub Copilot, OpenCode Go, Gemini, and the balance-style API providers (OpenAI, DeepSeek, OpenRouter, ...) — from the credentials the local CLIs already have. It ships three ways from one crate:

- **A Rust library** — pull it in as a dependency and read quotas programmatically, with your own HTTP client if you want one.
- **A CLI** — `limits status`, `limits --json` for status bars, `limits config ...` to manage providers.
- **A full-screen TUI** — `limits tui`, a live dashboard with per-second countdowns, sparkline trends, and a background refresh loop.

---

## 💻 Installation

### Requirements

- **Rust 1.97+**
- **`curl`** on `PATH` (present by default on Windows 10 1803+, macOS, and effectively every Linux install) — the default transport shells out to it rather than pulling in an async HTTP stack.

### Installing the binary

```bash
cargo install --git https://github.com/euxaristia/limits limits
```

### Building from source

```bash
git clone https://github.com/euxaristia/limits.git
cd limits

cargo build --release
./target/release/limits status
./target/release/limits tui
```

---

## 🛠️ CLI usage

```text
USAGE:
  limits [command] [options]

COMMANDS:
  status (default)        Fetch and show current usage for all enabled providers
  tui, watch              Full-screen live dashboard with continuous updates
  providers                List all providers and whether they are enabled
  config path              Show the active config file path
  config show               Show the active configuration
  config enable <id>       Enable a provider
  config disable <id>      Disable a provider
  config set-key <id> <k>  Set a provider's API key
  help, -h, --help         Show this message
  version, -v, --version   Show the version

OPTIONS:
  --json                  Emit JSON, for status bars and scripts
  -p, --provider <id>     Limit output to one provider
  -a, --all               Include providers that reported an error
  -i, --interval <secs>   Refresh interval for the dashboard (default 60)
  --no-color              Disable coloured output
```

```bash
# View enabled provider status in a formatted table
limits status

# JSON for Waybar, tmux, or a custom status bar
limits --json

# Just one provider
limits status -p opencode

# Live dashboard, polling every 30 seconds
limits tui -i 30

# Enable a provider and set its key
limits config enable opencode
limits config set-key opencode sk-...
```

### The dashboard

`limits tui` (or `limits watch`) opens a full-screen view: a provider list on the left, ranked by urgency, and a detail pane on the right with a gauge and a resets-in countdown per window, ticking live between reads. Keys: `j`/`k` or the arrows to select, `r` to refresh now, `a` to toggle hidden errors, `+`/`-` to change the poll interval, `q` to quit.

---

## 📦 Using it as a library

```rust
let limits = limits::Limits::new();
for usage in limits.snapshot() {
    println!("{}: {:.0}% used", usage.display_name, usage.peak_percent());
}
```

Nothing in this crate opens a socket directly — every request goes through the [`HttpClient`](src/http.rs) trait. Without a custom implementation, [`CurlClient`] is used, which needs only `curl` on `PATH`. A host application with its own hardened HTTP path (like [cairn-code](#cairn-code-integration)) can supply its own client instead of pulling in a second stack:

```rust
use limits::{HttpClient, HttpError, HttpRequest, HttpResponse, Limits};

struct HostClient;

impl HttpClient for HostClient {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
        // Delegate to the application's own client.
        todo!()
    }
}

let limits = Limits::with_client(HostClient);
let usage = limits.fetch(limits::Provider::OpenCode);
```

There is no async runtime anywhere in this crate: reads are blocking and run concurrently on plain OS threads (`Limits::snapshot` spawns one per enabled provider and joins them).

### cairn-code integration

`limits` was built to be embedded in [cairn-code](https://github.com/euxaristia/cairn-code) as the quota-checking backend: cairn-code implements `HttpClient` over its own hardened `curl` wrapper and calls `Limits::with_client(...)` so quota reads go through the same request path (timeouts, header hardening, control-character checks) as every other network call it makes, rather than opening a second one.

### Feature flags

| Feature | Default | Pulls in                       | What it gives you                          |
|---------|---------|---------------------------------|---------------------------------------------|
| `cli`   | on      | —                                | `limits::cli` and argument/terminal helpers |
| `tui`   | on      | `ratatui` (implies `cli`)       | `limits::tui`, the full-screen dashboard    |

A pure library consumer (like cairn-code) takes `default-features = false` and gets only the model, config, credentials, parsers, and fetch logic — no `ratatui`, no terminal-rendering code, no CLI argument parsing.

---

## 🏗️ Architecture

```
limits/
├── src/
│   ├── lib.rs           # Limits: the top-level library entry point
│   ├── model.rs         # Provider, ProviderConfig, ProviderUsage, UsageWindow
│   ├── config.rs         # Config file location, load/save, migration
│   ├── credentials/      # Reading local CLI credential stores + OS keyring + refresh probes
│   ├── http.rs           # The HttpClient trait + the default CurlClient
│   ├── fetch.rs           # Per-provider usage fetching over HttpClient
│   ├── parsers/           # Response parsing per provider (unit tested against fixtures)
│   ├── sort.rs             # Display ordering: usable first, exhausted by soonest reset
│   ├── time.rs             # RFC 3339 parsing and countdown formatting, no date crate
│   ├── redact.rs           # Email masking for footers and JSON output
│   ├── cli.rs (feature)     # Argument parsing, ANSI rendering, the `status` report
│   └── tui.rs (feature)     # The full-screen ratatui dashboard
└── Cargo.toml
```

---

## Supported providers

| Provider | Credential source |
|---|---|
| Claude | `~/.claude/.credentials.json` (Claude Code CLI), or a session cookie / API key |
| Codex | `~/.codex/auth.json` (`codex login`) |
| Antigravity | OS keyring, or `~/.gemini/antigravity-cli`, `~/.config/antigravity`, `~/.config/agy` |
| Grok | `~/.grok/auth.json` |
| Gemini | `~/.gemini/oauth_creds.json` |
| GitHub Copilot | `gh auth token`, or an API key |
| OpenCode Go | `OPENCODE_GO_API_KEY` / `OPENCODE_API_KEY`, the OS keyring entry cairn-code writes, `~/.local/share/opencode/auth.json`, or an API key in config |
| OpenAI, DeepSeek, OpenRouter | API key in config |

For OAuth-backed providers (Claude, Antigravity, Grok, Gemini, Copilot), `limits` will run the provider's own CLI headlessly to force a token refresh if the stored one has expired, before giving up.

---

## 📄 License

MIT License.
