//! Where the config file lives, and how it is read and written.

use crate::model::{LimitsConfig, Provider, ProviderConfig};
use std::io;
use std::path::{Path, PathBuf};

/// The user's home directory.
///
/// `HOME` is checked before `USERPROFILE` on every platform so a test (or a
/// caller running a probe CLI in a sandbox) can redirect credential and config
/// lookups by setting one variable.
pub fn home_dir() -> PathBuf {
    for var in ["HOME", "USERPROFILE"] {
        if let Some(value) = std::env::var_os(var)
            && !value.is_empty()
        {
            return PathBuf::from(value);
        }
    }
    PathBuf::from(".")
}

/// The config file this process will read and write.
///
/// `LIMITS_CONFIG` wins outright. Otherwise the XDG location is used, falling
/// back to the old `codexbar` path when that is the only one present, so an
/// existing install keeps its settings without a migration step.
pub fn config_path() -> PathBuf {
    for var in ["LIMITS_CONFIG", "CODEXBAR_CONFIG"] {
        if let Some(value) = std::env::var_os(var)
            && !value.is_empty()
        {
            return PathBuf::from(value);
        }
    }

    let base = std::env::var_os("XDG_CONFIG_HOME")
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(|| home_dir().join(".config"));

    let limits = base.join("limits").join("config.json");
    if limits.exists() {
        return limits;
    }
    let codexbar = base.join("codexbar").join("config.json");
    if codexbar.exists() {
        return codexbar;
    }
    limits
}

/// Read the config, writing a default one first if none exists.
///
/// Never fails: a machine with an unreadable or corrupt config still needs to
/// report quotas, so a bad file falls back to defaults rather than aborting.
pub fn load() -> LimitsConfig {
    let path = config_path();
    let Ok(data) = std::fs::read_to_string(&path) else {
        let fresh = LimitsConfig::default();
        let _ = save(&fresh);
        return fresh;
    };

    match serde_json::from_str::<LimitsConfig>(&data) {
        Ok(config) if !config.providers.is_empty() => migrate(config),
        _ => LimitsConfig::default(),
    }
}

/// Append providers this build knows about that the file predates.
///
/// New entries arrive enabled, matching a fresh config: a provider that reads a
/// credential the user already has should start reporting without being
/// switched on by hand.
fn migrate(mut config: LimitsConfig) -> LimitsConfig {
    for provider in Provider::ALL {
        if config.get(provider.id()).is_none() {
            config
                .providers
                .push(ProviderConfig::new(provider.id(), true));
        }
    }
    config
}

/// Write the config, creating its directory if needed.
pub fn save(config: &LimitsConfig) -> io::Result<()> {
    save_to(&config_path(), config)
}

/// Write a config to an explicit path.
pub fn save_to(path: &Path, config: &LimitsConfig) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let data = serde_json::to_string_pretty(config)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    std::fs::write(path, data)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn migration_adds_providers_the_file_predates() {
        let old = LimitsConfig {
            version: 1,
            providers: vec![ProviderConfig::new("claude", true)],
        };
        let migrated = migrate(old);

        assert!(migrated.get("opencode").is_some());
        assert!(migrated.get("copilot").is_some());
        assert!(migrated.get("grok").is_some());
        assert_eq!(migrated.providers.len(), Provider::ALL.len());
    }

    #[test]
    fn migration_leaves_existing_entries_untouched() {
        let mut disabled = ProviderConfig::new("claude", false);
        disabled.api_key = "secret".into();
        let migrated = migrate(LimitsConfig {
            version: 1,
            providers: vec![disabled],
        });

        let claude = migrated.get("claude").unwrap();
        assert!(!claude.is_enabled());
        assert_eq!(claude.api_key, "secret");
        assert_eq!(migrated.providers[0].id, "claude");
    }

    #[test]
    fn config_round_trips_through_disk() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("nested").join("config.json");

        let mut config = LimitsConfig::default();
        config.get_mut("Grok").unwrap().api_key = "xai-test".into();
        save_to(&path, &config).unwrap();

        let reloaded: LimitsConfig =
            serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(reloaded.get("grok").unwrap().api_key, "xai-test");
    }
}
