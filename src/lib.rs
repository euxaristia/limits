//! Every AI coding limit and quota, in one place.
//!
//! `limits` reads how much of each AI subscription is left — Claude, Codex,
//! Antigravity, Grok, GitHub Copilot, OpenCode Go, and the balance-style API
//! providers — from the credentials the local CLIs have already stored. It
//! ships as a library, a CLI, and a live full-screen dashboard.
//!
//! # Using it as a library
//!
//! ```no_run
//! let limits = limits::Limits::new();
//! for usage in limits.snapshot() {
//!     println!("{}: {:.0}% used", usage.display_name, usage.peak_percent());
//! }
//! ```
//!
//! # Supplying your own transport
//!
//! Nothing here opens a socket directly. Requests go through [`HttpClient`], so
//! a host application with its own hardened HTTP path can route quota reads
//! through it instead of pulling in a second stack:
//!
//! ```no_run
//! use limits::{HttpClient, HttpError, HttpRequest, HttpResponse, Limits};
//!
//! struct HostClient;
//!
//! impl HttpClient for HostClient {
//!     fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
//!         # let _ = request;
//!         // Delegate to the application's own client.
//!         # unimplemented!()
//!     }
//! }
//!
//! let limits = Limits::with_client(HostClient);
//! let usage = limits.fetch(limits::Provider::OpenCode);
//! ```
//!
//! Without a custom client, [`CurlClient`] is used, which needs only `curl` on
//! `PATH`. There is no async runtime anywhere in this crate: reads are blocking
//! and run concurrently on plain threads.

pub mod config;
pub mod credentials;
pub mod fetch;
pub mod http;
pub mod model;
pub mod parsers;
pub mod redact;
pub mod sort;
pub mod time;

#[cfg(feature = "cli")]
pub mod cli;
#[cfg(feature = "tui")]
pub mod tui;

pub use http::{CurlClient, HttpClient, HttpError, HttpRequest, HttpResponse, Method};
pub use model::{LimitsConfig, Provider, ProviderConfig, ProviderUsage, Status, UsageWindow};

pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// A configured reader for provider quotas.
///
/// Holds the config and the transport. Reading is `&self`, so one instance can
/// be shared across threads and polled on a timer.
pub struct Limits {
    config: LimitsConfig,
    client: Box<dyn HttpClient>,
}

impl Limits {
    /// Read the config from disk and use the default `curl` transport.
    pub fn new() -> Self {
        Limits {
            config: config::load(),
            client: Box::new(CurlClient::new()),
        }
    }

    /// Use a caller-supplied transport, with the config still read from disk.
    pub fn with_client(client: impl HttpClient + 'static) -> Self {
        Limits {
            config: config::load(),
            client: Box::new(client),
        }
    }

    /// Replace the config, for a caller that manages it itself rather than
    /// through `~/.config/limits/config.json`.
    pub fn with_config(mut self, config: LimitsConfig) -> Self {
        self.config = config;
        self
    }

    pub fn config(&self) -> &LimitsConfig {
        &self.config
    }

    pub fn config_mut(&mut self) -> &mut LimitsConfig {
        &mut self.config
    }

    /// Read every enabled provider, concurrently, ordered for display.
    pub fn snapshot(&self) -> Vec<ProviderUsage> {
        self.snapshot_filtered(|_| true)
    }

    /// Read the enabled providers whose config passes `keep`.
    pub fn snapshot_filtered(&self, keep: impl Fn(&ProviderConfig) -> bool) -> Vec<ProviderUsage> {
        let mut configs: Vec<ProviderConfig> = self
            .config
            .enabled()
            .filter(|config| keep(config))
            .cloned()
            .collect();
        sort::sort_configs(&mut configs);

        let mut results = fetch::fetch_all(self.client.as_ref(), &configs);
        sort::sort_results(&mut results);
        results
    }

    /// Read one provider, whether or not it is enabled in the config.
    ///
    /// A provider with no config entry is read with its defaults, so an
    /// embedder that never writes a config file can still ask for one reading.
    pub fn fetch(&self, provider: Provider) -> ProviderUsage {
        let config = self
            .config
            .get(provider.id())
            .cloned()
            .unwrap_or_else(|| ProviderConfig::new(provider.id(), true));
        fetch::Fetcher::new(self.client.as_ref()).fetch(&config)
    }
}

impl Default for Limits {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    struct Recorder(Mutex<Vec<String>>);

    impl HttpClient for Recorder {
        fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
            self.0.lock().unwrap().push(request.url.clone());
            Err(HttpError("offline".into()))
        }
    }

    fn only(id: &str) -> LimitsConfig {
        LimitsConfig {
            version: 1,
            providers: vec![ProviderConfig::new(id, true)],
        }
    }

    #[test]
    fn a_snapshot_covers_only_enabled_providers() {
        let mut config = LimitsConfig::default();
        for provider in config.providers.iter_mut() {
            provider.enabled = Some(provider.id == "openrouter");
        }
        config.get_mut("openrouter").unwrap().api_key = "sk-or".into();

        let limits = Limits::with_client(Recorder(Mutex::new(Vec::new()))).with_config(config);
        let snapshot = limits.snapshot();

        assert_eq!(snapshot.len(), 1);
        assert_eq!(snapshot[0].id, "openrouter");
    }

    #[test]
    fn a_disabled_provider_can_still_be_read_on_request() {
        let mut config = only("openrouter");
        config.providers[0].enabled = Some(false);
        config.providers[0].api_key = "sk-or".into();

        let limits = Limits::with_client(Recorder(Mutex::new(Vec::new()))).with_config(config);

        assert!(limits.snapshot().is_empty());
        assert_eq!(limits.fetch(Provider::OpenRouter).id, "openrouter");
    }

    #[test]
    fn a_provider_absent_from_the_config_is_read_with_defaults() {
        let limits =
            Limits::with_client(Recorder(Mutex::new(Vec::new()))).with_config(only("claude"));
        // No entry for OpenCode at all: the read still returns a reading rather
        // than panicking or silently doing nothing.
        assert_eq!(limits.fetch(Provider::OpenCode).id, "opencode");
    }

    #[test]
    fn snapshots_can_be_narrowed_to_one_provider() {
        let mut config = LimitsConfig::default();
        for provider in config.providers.iter_mut() {
            provider.api_key = "k".into();
        }
        let limits = Limits::with_client(Recorder(Mutex::new(Vec::new()))).with_config(config);

        let narrowed = limits.snapshot_filtered(|c| c.id.eq_ignore_ascii_case("openai"));
        assert_eq!(narrowed.len(), 1);
        assert_eq!(narrowed[0].id, "openai");
    }
}
