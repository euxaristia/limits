//! The vocabulary every other module speaks: which providers exist, how a
//! provider is configured, and what a usage reading looks like once taken.
//!
//! The serialised field names here are load-bearing. `limits --json` feeds
//! status bars (Waybar, tmux, SketchyBar) whose scripts index these keys by
//! name, so they match the shapes the Go implementation emitted rather than
//! Rust naming convention.

use serde::{Deserialize, Serialize};
use std::fmt;

/// A service whose usage `limits` knows how to read.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Provider {
    Codex,
    OpenAi,
    Claude,
    Cursor,
    Gemini,
    DeepSeek,
    OpenRouter,
    ElevenLabs,
    Groq,
    Bedrock,
    Antigravity,
    Grok,
    Copilot,
    OpenCode,
    Unknown,
}

impl Provider {
    /// Every provider that can appear in a config file, in the order a fresh
    /// config lists them.
    pub const ALL: [Provider; 14] = [
        Provider::Codex,
        Provider::OpenAi,
        Provider::Claude,
        Provider::Cursor,
        Provider::Gemini,
        Provider::DeepSeek,
        Provider::OpenRouter,
        Provider::ElevenLabs,
        Provider::Groq,
        Provider::Bedrock,
        Provider::Antigravity,
        Provider::Grok,
        Provider::Copilot,
        Provider::OpenCode,
    ];

    /// The stable identifier used in config files, CLI flags, and JSON output.
    pub fn id(self) -> &'static str {
        match self {
            Provider::Codex => "codex",
            Provider::OpenAi => "openai",
            Provider::Claude => "claude",
            Provider::Cursor => "cursor",
            Provider::Gemini => "gemini",
            Provider::DeepSeek => "deepseek",
            Provider::OpenRouter => "openrouter",
            Provider::ElevenLabs => "elevenlabs",
            Provider::Groq => "groq",
            Provider::Bedrock => "bedrock",
            Provider::Antigravity => "antigravity",
            Provider::Grok => "grok",
            Provider::Copilot => "copilot",
            Provider::OpenCode => "opencode",
            Provider::Unknown => "unknown",
        }
    }

    /// The name shown to a person.
    pub fn display_name(self) -> &'static str {
        match self {
            Provider::Codex => "Codex",
            Provider::OpenAi => "OpenAI",
            Provider::Claude => "Claude",
            Provider::Cursor => "Cursor",
            Provider::Gemini => "Gemini",
            Provider::DeepSeek => "DeepSeek",
            Provider::OpenRouter => "OpenRouter",
            Provider::ElevenLabs => "ElevenLabs",
            Provider::Groq => "Groq",
            Provider::Bedrock => "AWS Bedrock",
            Provider::Antigravity => "Antigravity",
            Provider::Grok => "Grok",
            Provider::Copilot => "GitHub Copilot",
            Provider::OpenCode => "OpenCode Go",
            Provider::Unknown => "Unknown",
        }
    }

    /// What a user must do before this provider can report anything.
    pub fn setup_hint(self) -> &'static str {
        match self {
            Provider::OpenAi => "API key required. Set with 'limits config set-key openai <key>'",
            Provider::Claude => {
                "API key or Claude CLI login required (~/.claude/.credentials.json)"
            }
            Provider::DeepSeek => {
                "API key required. Set with 'limits config set-key deepseek <key>'"
            }
            Provider::OpenRouter => {
                "API key required. Set with 'limits config set-key openrouter <key>'"
            }
            Provider::ElevenLabs => {
                "API key required. Set with 'limits config set-key elevenlabs <key>'"
            }
            Provider::Groq => "API key required. Set with 'limits config set-key groq <key>'",
            Provider::Bedrock => "AWS credentials required",
            Provider::Cursor => "API key or token required",
            Provider::Codex => {
                "ChatGPT login required. Run 'codex login' to create ~/.codex/auth.json"
            }
            Provider::Copilot => {
                "API key and organization required. Set with 'limits config set-key copilot <token>' and set the provider 'region' to your org name"
            }
            Provider::OpenCode => {
                "OpenCode Go key required. Set OPENCODE_GO_API_KEY or run 'limits config set-key opencode <key>'"
            }
            _ => "Credentials or API key required",
        }
    }
}

impl From<&str> for Provider {
    fn from(value: &str) -> Self {
        match value.to_ascii_lowercase().as_str() {
            "codex" => Provider::Codex,
            "openai" => Provider::OpenAi,
            "claude" => Provider::Claude,
            "cursor" => Provider::Cursor,
            "gemini" => Provider::Gemini,
            "deepseek" => Provider::DeepSeek,
            "openrouter" => Provider::OpenRouter,
            "elevenlabs" => Provider::ElevenLabs,
            "groq" => Provider::Groq,
            "bedrock" => Provider::Bedrock,
            "antigravity" => Provider::Antigravity,
            "grok" => Provider::Grok,
            "copilot" => Provider::Copilot,
            "opencode" => Provider::OpenCode,
            _ => Provider::Unknown,
        }
    }
}

impl fmt::Display for Provider {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.id())
    }
}

/// How a reading turned out. Kept as a small enum rather than the free-form
/// string the Go version used, so a caller cannot typo `"degrade"` and have it
/// silently mean "healthy".
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    /// Numbers were read successfully.
    Healthy,
    /// The provider answered, but not usefully (HTTP error, unexpected shape).
    Degraded,
    /// No credentials at all: nothing was attempted.
    Unconfigured,
}

impl Status {
    pub fn as_str(self) -> &'static str {
        match self {
            Status::Healthy => "healthy",
            Status::Degraded => "degraded",
            Status::Unconfigured => "unconfigured",
        }
    }
}

/// One provider's entry in the config file.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct ProviderConfig {
    pub id: String,
    /// Absent means disabled: a provider added by a newer version of the file
    /// format should not start reaching out to the network unasked.
    #[serde(default)]
    pub enabled: Option<bool>,
    #[serde(default, rename = "apiKey")]
    pub api_key: String,
    #[serde(default, rename = "cookieHeader")]
    pub cookie_header: String,
    #[serde(default)]
    pub region: String,
}

impl ProviderConfig {
    pub fn new(id: &str, enabled: bool) -> Self {
        ProviderConfig {
            id: id.to_string(),
            enabled: Some(enabled),
            ..Default::default()
        }
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled.unwrap_or(false)
    }

    pub fn provider(&self) -> Provider {
        Provider::from(self.id.as_str())
    }

    pub fn has_api_key(&self) -> bool {
        !self.api_key.trim().is_empty()
    }

    pub fn has_cookie(&self) -> bool {
        !self.cookie_header.trim().is_empty()
    }
}

/// The whole config file.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LimitsConfig {
    pub version: u32,
    pub providers: Vec<ProviderConfig>,
}

impl LimitsConfig {
    /// Look up a provider's entry, case-insensitively on the id.
    pub fn get(&self, id: &str) -> Option<&ProviderConfig> {
        self.providers
            .iter()
            .find(|p| p.id.eq_ignore_ascii_case(id))
    }

    pub fn get_mut(&mut self, id: &str) -> Option<&mut ProviderConfig> {
        self.providers
            .iter_mut()
            .find(|p| p.id.eq_ignore_ascii_case(id))
    }

    pub fn enabled(&self) -> impl Iterator<Item = &ProviderConfig> {
        self.providers.iter().filter(|p| p.is_enabled())
    }
}

impl Default for LimitsConfig {
    fn default() -> Self {
        LimitsConfig {
            version: 1,
            providers: Provider::ALL
                .iter()
                .map(|p| ProviderConfig::new(p.id(), true))
                .collect(),
        }
    }
}

/// One rolling allowance: a session window, a weekly cap, a credit balance.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct UsageWindow {
    #[serde(rename = "Label")]
    pub label: String,
    #[serde(rename = "UsedPercent")]
    pub used_percent: f64,
    /// Human-readable time to reset, e.g. `3d 2h`, `45m`, `Resets now`.
    #[serde(rename = "ResetCountdown")]
    pub reset_countdown: String,
    /// Length of the window in seconds, or 0 when the provider does not say.
    #[serde(rename = "WindowSeconds")]
    pub window_seconds: i64,
    /// Replaces the rendered percentage when the provider's own wording is more
    /// informative than a bare number (`Unlimited`, `4 / 200 (2.0% used)`).
    #[serde(rename = "PercentTextOverride")]
    pub percent_text_override: Option<String>,
}

impl UsageWindow {
    pub fn new(label: impl Into<String>, used_percent: f64) -> Self {
        UsageWindow {
            label: label.into(),
            used_percent: used_percent.clamp(0.0, 100.0),
            ..Default::default()
        }
    }

    pub fn reset(mut self, countdown: impl Into<String>) -> Self {
        self.reset_countdown = countdown.into();
        self
    }

    pub fn seconds(mut self, seconds: i64) -> Self {
        self.window_seconds = seconds;
        self
    }

    pub fn text(mut self, text: impl Into<String>) -> Self {
        self.percent_text_override = Some(text.into());
        self
    }

    /// What to print in place of the percentage.
    pub fn percent_text(&self) -> String {
        match &self.percent_text_override {
            Some(text) => text.clone(),
            None => format!("{:.1}%", self.used_percent),
        }
    }

    pub fn is_spent(&self) -> bool {
        self.used_percent >= 100.0
    }
}

/// A complete reading for one provider.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ProviderUsage {
    #[serde(rename = "Provider")]
    pub provider: String,
    #[serde(rename = "Id")]
    pub id: String,
    #[serde(rename = "DisplayName")]
    pub display_name: String,
    #[serde(rename = "Windows")]
    pub windows: Vec<UsageWindow>,
    #[serde(rename = "Status")]
    pub status: Status,
    #[serde(rename = "IsMock")]
    pub is_mock: bool,
    #[serde(rename = "HasError")]
    pub has_error: bool,
    #[serde(rename = "ErrorMessage")]
    pub error_message: String,
    #[serde(rename = "Footer")]
    pub footer: String,
}

impl ProviderUsage {
    /// A healthy reading with the given windows.
    pub fn healthy(
        provider: Provider,
        windows: Vec<UsageWindow>,
        footer: impl Into<String>,
    ) -> Self {
        ProviderUsage {
            provider: provider.id().to_string(),
            id: provider.id().to_string(),
            display_name: provider.display_name().to_string(),
            windows,
            status: Status::Healthy,
            is_mock: false,
            has_error: false,
            error_message: String::new(),
            footer: crate::redact::redact_emails(&footer.into()),
        }
    }

    /// The provider answered, but not with numbers we can use.
    pub fn degraded(provider: Provider, message: impl Into<String>) -> Self {
        ProviderUsage {
            provider: provider.id().to_string(),
            id: provider.id().to_string(),
            display_name: provider.display_name().to_string(),
            windows: Vec::new(),
            status: Status::Degraded,
            is_mock: false,
            has_error: true,
            error_message: crate::redact::redact_emails(&message.into()),
            footer: String::new(),
        }
    }

    /// No credentials were found, so nothing was attempted.
    pub fn unconfigured(provider: Provider) -> Self {
        ProviderUsage {
            provider: provider.id().to_string(),
            id: provider.id().to_string(),
            display_name: provider.display_name().to_string(),
            windows: Vec::new(),
            status: Status::Unconfigured,
            is_mock: false,
            has_error: true,
            error_message: provider.setup_hint().to_string(),
            footer: String::new(),
        }
    }

    /// A single window derived from a used/limit pair, as balance-style
    /// providers report it.
    pub fn from_balance(
        provider: Provider,
        used: f64,
        limit: f64,
        reset: &str,
        footer: impl Into<String>,
    ) -> Self {
        let percent = if limit > 0.0 {
            ((used / limit) * 100.0).clamp(0.0, 100.0)
        } else {
            0.0
        };
        ProviderUsage::healthy(
            provider,
            vec![UsageWindow::new("Quota", percent).reset(reset)],
            footer,
        )
    }

    /// True when every window this provider reports is spent, so nothing here
    /// is usable until something resets. A provider with no windows at all is
    /// not exhausted: it simply said nothing.
    pub fn is_exhausted(&self) -> bool {
        !self.windows.is_empty() && self.windows.iter().all(UsageWindow::is_spent)
    }

    /// The highest utilisation across windows, which is the number that decides
    /// whether the next request goes through.
    pub fn peak_percent(&self) -> f64 {
        self.windows
            .iter()
            .map(|w| w.used_percent)
            .fold(0.0_f64, f64::max)
    }

    /// The provider's own enum, recovered from the serialised id.
    pub fn provider_kind(&self) -> Provider {
        Provider::from(self.id.as_str())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn provider_ids_round_trip() {
        for provider in Provider::ALL {
            assert_eq!(Provider::from(provider.id()), provider);
        }
    }

    #[test]
    fn provider_ids_are_case_insensitive() {
        assert_eq!(Provider::from("OpenCode"), Provider::OpenCode);
        assert_eq!(Provider::from("ANTIGRAVITY"), Provider::Antigravity);
        assert_eq!(Provider::from("nonesuch"), Provider::Unknown);
    }

    #[test]
    fn default_config_enables_every_provider() {
        let config = LimitsConfig::default();
        assert_eq!(config.providers.len(), Provider::ALL.len());
        assert!(config.providers.iter().all(ProviderConfig::is_enabled));
        assert!(config.get("opencode").is_some());
    }

    #[test]
    fn provider_missing_enabled_flag_stays_off() {
        let config: LimitsConfig =
            serde_json::from_str(r#"{"version":1,"providers":[{"id":"claude"}]}"#).unwrap();
        assert!(!config.providers[0].is_enabled());
    }

    #[test]
    fn exhausted_requires_every_window_spent() {
        let spent =
            ProviderUsage::healthy(Provider::Grok, vec![UsageWindow::new("Weekly", 100.0)], "");
        assert!(spent.is_exhausted());

        let mixed = ProviderUsage::healthy(
            Provider::Antigravity,
            vec![
                UsageWindow::new("Session Gemini", 2.0),
                UsageWindow::new("Weekly Claude & GPT", 100.0),
            ],
            "",
        );
        assert!(!mixed.is_exhausted());

        let silent = ProviderUsage::healthy(Provider::Codex, vec![], "");
        assert!(!silent.is_exhausted());
    }

    #[test]
    fn json_keys_match_the_status_bar_contract() {
        let usage = ProviderUsage::healthy(
            Provider::Claude,
            vec![
                UsageWindow::new("Session", 42.0)
                    .reset("2h 5m")
                    .seconds(18000),
            ],
            "Claude CLI",
        );
        let json = serde_json::to_string(&usage).unwrap();
        for key in [
            "\"Provider\"",
            "\"Id\"",
            "\"DisplayName\"",
            "\"Windows\"",
            "\"Status\"",
            "\"IsMock\"",
            "\"HasError\"",
            "\"ErrorMessage\"",
            "\"Footer\"",
            "\"Label\"",
            "\"UsedPercent\"",
            "\"ResetCountdown\"",
            "\"WindowSeconds\"",
            "\"PercentTextOverride\"",
        ] {
            assert!(json.contains(key), "missing {key} in {json}");
        }
        assert!(json.contains("\"healthy\""));
    }

    #[test]
    fn balance_windows_clamp_out_of_range_input() {
        let over = ProviderUsage::from_balance(Provider::OpenAi, 150.0, 100.0, "N/A", "");
        assert_eq!(over.windows[0].used_percent, 100.0);

        let no_limit = ProviderUsage::from_balance(Provider::OpenAi, 5.0, 0.0, "N/A", "");
        assert_eq!(no_limit.windows[0].used_percent, 0.0);
    }
}
