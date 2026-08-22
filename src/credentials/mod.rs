//! Finding the credentials the local AI CLIs have already stored, and getting
//! them refreshed when they have gone stale.
//!
//! `limits` never asks the user to paste a token that a CLI on the same machine
//! already holds. It reads `codex login`, `claude`, `gemini`, `grok`, and
//! Antigravity's own stores directly. Nothing here writes a credential; the
//! only mutation is asking a provider's own CLI to refresh its own token.

pub mod headless;
pub mod keyring;

use crate::config::home_dir;
use crate::model::Provider;
use crate::time::{now_unix, parse_rfc3339, unix_from_number};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

/// How long before expiry a token is already treated as spent. A token with
/// two seconds left will be rejected by the time the request lands.
const EXPIRY_MARGIN: i64 = 30;

/// How long a refresh probe is allowed to run before it is killed.
const PROBE_TIMEOUT: Duration = Duration::from_secs(10);
/// How often the probe is checked for having exited.
const PROBE_POLL: Duration = Duration::from_millis(25);

/// A credential resolved from a local CLI's store.
///
/// One type covers every provider: each loader fills the fields its provider
/// supplies and leaves the rest empty. Splitting it per provider would
/// duplicate the freshness check five times, which is exactly the logic that
/// must not drift.
#[derive(Clone, Debug, Default)]
pub struct Token {
    pub access_token: String,
    pub refresh_token: String,
    /// Expiry in Unix seconds. `None` means the store did not say, which is
    /// treated as "probably fine" rather than "expired".
    pub expires_at: Option<i64>,
    pub email: String,
    /// ChatGPT account id, sent by the Codex usage endpoint as a header.
    pub account_id: String,
    /// Subscription plan as the provider names it (`plus`, `max`, `pro`).
    pub plan_type: String,
    /// How the session was established (`oauth`, `oidc`, `api_key`).
    pub auth_method: String,
}

impl Token {
    pub fn is_usable(&self) -> bool {
        !self.access_token.trim().is_empty()
    }

    /// True when the token can still be used for a request.
    pub fn is_fresh(&self) -> bool {
        if !self.is_usable() {
            return false;
        }
        match self.expires_at {
            Some(expiry) => expiry > now_unix() + EXPIRY_MARGIN,
            None => true,
        }
    }
}

/// Read and parse a JSON file, returning `None` for anything unreadable.
fn read_json(path: &Path) -> Option<Value> {
    serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()
}

/// Pull a non-empty string out of a JSON object.
fn string_at(value: &Value, key: &str) -> Option<String> {
    value
        .get(key)?
        .as_str()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

/// Read an expiry that may be an RFC 3339 string, Unix seconds, or Unix
/// milliseconds. Every store this crate reads uses a different one.
fn expiry_at(value: &Value, keys: &[&str]) -> Option<i64> {
    for key in keys {
        match value.get(key) {
            Some(Value::String(text)) => {
                if let Some(unix) = parse_rfc3339(text) {
                    return Some(unix);
                }
            }
            Some(Value::Number(number)) => {
                if let Some(raw) = number.as_f64() {
                    return Some(unix_from_number(raw));
                }
            }
            _ => {}
        }
    }
    None
}

/// First non-empty value among the given keys, checked in each object in turn.
fn first_string(objects: &[&Value], keys: &[&str]) -> String {
    for object in objects {
        for key in keys {
            if let Some(found) = string_at(object, key) {
                return found;
            }
        }
    }
    String::new()
}

/// Decode standard or URL-safe base64, with or without padding.
///
/// Used for JWT payloads and go-keyring blobs. Both are small and local, so a
/// dependency for this would cost more than it saves.
pub(crate) fn base64_decode(input: &str) -> Option<Vec<u8>> {
    let mut out = Vec::with_capacity(input.len() * 3 / 4);
    let mut accumulator: u32 = 0;
    let mut bits = 0u32;

    for byte in input.bytes() {
        let value = match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a' + 26,
            b'0'..=b'9' => byte - b'0' + 52,
            b'+' | b'-' => 62,
            b'/' | b'_' => 63,
            b'=' => break,
            b'\n' | b'\r' => continue,
            _ => return None,
        };
        accumulator = (accumulator << 6) | u32::from(value);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((accumulator >> bits) as u8);
        }
    }
    Some(out)
}

/// Read the `email` claim from a JWT without verifying it.
///
/// The signature is irrelevant here: the token came from the user's own disk
/// and the claim is used only to label the account in a footer.
pub(crate) fn email_from_jwt(jwt: &str) -> String {
    let Some(payload) = jwt.split('.').nth(1) else {
        return String::new();
    };
    let Some(decoded) = base64_decode(payload) else {
        return String::new();
    };
    let Ok(claims) = serde_json::from_slice::<Value>(&decoded) else {
        return String::new();
    };

    if let Some(email) = string_at(&claims, "email") {
        return email;
    }
    claims
        .get("https://api.openai.com/profile")
        .and_then(|profile| string_at(profile, "email"))
        .unwrap_or_default()
}

/// The ChatGPT session written by `codex login`.
pub fn load_codex() -> Option<Token> {
    let root = read_json(&home_dir().join(".codex").join("auth.json"))?;
    let tokens = root.get("tokens").cloned().unwrap_or(Value::Null);

    let access_token = string_at(&tokens, "access_token")?;
    let id_token = string_at(&tokens, "id_token").unwrap_or_default();

    let mut email = first_string(&[&root, &tokens], &["email"]);
    if email.is_empty() {
        email = email_from_jwt(&id_token);
    }
    if email.is_empty() {
        email = email_from_jwt(&access_token);
    }

    Some(Token {
        access_token,
        account_id: string_at(&tokens, "account_id").unwrap_or_default(),
        plan_type: string_at(&tokens, "plan_type").unwrap_or_default(),
        email,
        ..Default::default()
    })
}

/// The OAuth session Claude Code keeps in `~/.claude/.credentials.json`,
/// falling back to the OS keyring entry from cairn-code if absent.
pub fn load_claude() -> Option<Token> {
    if let Some(root) = read_json(&home_dir().join(".claude").join(".credentials.json"))
        && let Some(token) = parse_claude(&root)
    {
        return Some(token);
    }

    for (service, account) in [
        ("cairn-code", "oauth:claude"),
        ("cairn-code", "claude"),
        ("claude", "credentials"),
    ] {
        if let Some(data) = keyring::read(service, account)
            && let Ok(value) = serde_json::from_slice::<Value>(&data)
            && let Some(token) = parse_claude(&value)
        {
            return Some(token);
        }
    }

    None
}

pub(crate) fn parse_claude(root: &Value) -> Option<Token> {
    if let Some(oauth) = root.get("claudeAiOauth") {
        return Some(Token {
            access_token: string_at(oauth, "accessToken")?,
            refresh_token: string_at(oauth, "refreshToken").unwrap_or_default(),
            expires_at: expiry_at(oauth, &["expiresAt", "expires_at"]),
            email: first_string(&[oauth, root], &["email"]),
            plan_type: first_string(&[oauth, root], &["subscriptionType"]),
            ..Default::default()
        });
    }

    // Direct OAuth token object (from keyring)
    if let Some(access_token) =
        string_at(root, "access_token").or_else(|| string_at(root, "accessToken"))
    {
        let refresh_token = string_at(root, "refresh_token")
            .or_else(|| string_at(root, "refreshToken"))
            .unwrap_or_default();
        let mut email = string_at(root, "email").unwrap_or_default();
        if email.is_empty() {
            email = email_from_jwt(&access_token);
        }
        return Some(Token {
            access_token,
            refresh_token,
            expires_at: expiry_at(root, &["expires_at", "expiresAt"]),
            email,
            plan_type: first_string(&[root], &["subscriptionType", "plan_type", "plan"]),
            auth_method: "oauth".into(),
            ..Default::default()
        });
    }

    None
}

/// The Google OAuth credentials the Gemini CLI writes.
pub fn load_gemini() -> Option<Token> {
    let root = read_json(&home_dir().join(".gemini").join("oauth_creds.json"))?;
    Some(Token {
        access_token: string_at(&root, "access_token")?,
        refresh_token: string_at(&root, "refresh_token").unwrap_or_default(),
        expires_at: expiry_at(&root, &["expiry", "expires_at", "expiry_date"]),
        ..Default::default()
    })
}

/// The Grok / xAI token from the Grok CLI's `~/.grok/auth.json` or the OS
/// keyring (cairn-code device OAuth / API key), whichever still holds a live
/// session.
///
/// Order alone is not enough here. Only the Grok CLI refreshes its own file,
/// and the refresh probe is what drives it; nothing rewrites the `cairn-code`
/// keyring copy. A stale keyring entry that shadowed the file would therefore
/// stay stale forever: every call would see an expired token, run the probe,
/// refresh the file it then ignored, and hand back the same dead token.
pub fn load_grok() -> Option<Token> {
    pick_live(grok_candidates())
}

/// The first still-live credential, or the canonical one when none are.
///
/// Falling back to the head of the list rather than `None` keeps an expired
/// token in play so the caller's refresh probe still has something to revive.
fn pick_live(candidates: Vec<Token>) -> Option<Token> {
    match candidates.iter().find(|token| token.is_fresh()) {
        Some(fresh) => Some(fresh.clone()),
        None => candidates.into_iter().next(),
    }
}

/// Every Grok credential this machine holds, canonical store first.
fn grok_candidates() -> Vec<Token> {
    let mut found = Vec::new();

    if let Some(root) = read_json(&home_dir().join(".grok").join("auth.json"))
        && let Some(token) = parse_grok(&root)
    {
        found.push(token);
    }

    for (service, account) in [
        ("cairn-code", "oauth:xai"),
        ("cairn-code", "xai"),
        ("grok", "auth"),
        ("grok", "oauth:grok"),
    ] {
        let Some(data) = keyring::read(service, account) else {
            continue;
        };
        match serde_json::from_slice::<Value>(&data) {
            Ok(value) => found.extend(parse_grok(&value)),
            Err(_) => {
                let key = String::from_utf8_lossy(&data).trim().to_string();
                if !key.is_empty() && !key.starts_with('{') {
                    found.push(Token {
                        access_token: key,
                        auth_method: "api_key".into(),
                        ..Default::default()
                    });
                }
            }
        }
    }

    found
}

pub(crate) fn parse_grok(root: &Value) -> Option<Token> {
    // 1. If root is a map of account entries (as in ~/.grok/auth.json):
    if let Some(entries) = root.as_object()
        && let Some(token) = entries.values().find_map(parse_grok_entry)
    {
        return Some(token);
    }
    // 2. Direct token object (as in cairn-code keyring or single entry)
    parse_grok_entry(root)
}

fn parse_grok_entry(entry: &Value) -> Option<Token> {
    // OAuth token format (cairn-code / device OAuth): "access_token"
    if let Some(access_token) =
        string_at(entry, "access_token").or_else(|| string_at(entry, "accessToken"))
    {
        let refresh_token = string_at(entry, "refresh_token")
            .or_else(|| string_at(entry, "refreshToken"))
            .unwrap_or_default();
        let mut email = string_at(entry, "email").unwrap_or_default();
        if email.is_empty() {
            email = email_from_jwt(&access_token);
        }
        let id_token = string_at(entry, "id_token").unwrap_or_default();
        if email.is_empty() && !id_token.is_empty() {
            email = email_from_jwt(&id_token);
        }
        return Some(Token {
            access_token,
            refresh_token,
            email,
            expires_at: expiry_at(entry, &["expires_at", "expiresAt", "expiry"]),
            auth_method: string_at(entry, "auth_mode")
                .or_else(|| string_at(entry, "token_type"))
                .unwrap_or_else(|| "oauth".into()),
            ..Default::default()
        });
    }

    // CLI format (~/.grok/auth.json): "key"
    if let Some(access_token) = string_at(entry, "key") {
        return Some(Token {
            access_token,
            email: string_at(entry, "email").unwrap_or_default(),
            expires_at: expiry_at(entry, &["expires_at", "expiresAt"]),
            auth_method: string_at(entry, "auth_mode").unwrap_or_else(|| "oidc".into()),
            ..Default::default()
        });
    }

    None
}

/// Antigravity's OAuth token, from the keyring first and the on-disk stores
/// after. Recent versions keep the live token in the keyring and leave the file
/// behind stale, so file-first would report a permanently expired session.
pub fn load_antigravity() -> Option<Token> {
    if let Some(data) = keyring::read("gemini", "antigravity")
        && let Ok(value) = serde_json::from_slice::<Value>(&data)
        && let Some(token) = parse_antigravity(&value)
    {
        return Some(token);
    }

    antigravity_paths()
        .into_iter()
        .filter_map(|path| read_json(&path))
        .find_map(|value| parse_antigravity(&value))
}

fn antigravity_paths() -> Vec<PathBuf> {
    let home = home_dir();
    let dirs = [
        home.join(".gemini").join("antigravity-cli"),
        home.join(".config").join("antigravity"),
        home.join(".config").join("agy"),
    ];
    // `agy` writes "antigravity-oauth-token"; older and alternate installs use
    // "credentials.json".
    let names = ["antigravity-oauth-token", "credentials.json"];
    dirs.iter()
        .flat_map(|dir| names.iter().map(move |name| dir.join(name)))
        .collect()
}

pub(crate) fn parse_antigravity(root: &Value) -> Option<Token> {
    let token = root.get("token")?;
    let access_token = string_at(token, "access_token")?;

    let mut email = first_string(&[root, token], &["email", "user_email"]);
    if email.is_empty() {
        let id_token = first_string(&[root, token], &["id_token"]);
        email = email_from_jwt(&id_token);
    }

    Some(Token {
        access_token,
        refresh_token: string_at(token, "refresh_token").unwrap_or_default(),
        expires_at: expiry_at(token, &["expiry", "expires_at"]),
        email,
        auth_method: string_at(root, "auth_method").unwrap_or_else(|| "unknown".into()),
        ..Default::default()
    })
}

/// Load whichever credential a provider uses, if any.
pub fn load(provider: Provider) -> Option<Token> {
    match provider {
        Provider::Codex => load_codex(),
        Provider::Claude => load_claude(),
        Provider::Gemini => load_gemini(),
        Provider::Grok => load_grok(),
        Provider::Antigravity => load_antigravity(),
        _ => None,
    }
}

/// Whether a provider's stored credential is currently usable.
///
/// Providers with no local store answer `true`: there is nothing here to
/// refresh, so a probe would be wasted.
pub fn is_token_working(provider: Provider) -> bool {
    match provider {
        Provider::Codex
        | Provider::Claude
        | Provider::Gemini
        | Provider::Grok
        | Provider::Antigravity => load(provider).is_some_and(|t| t.is_fresh()),
        // `gh` has no expiring token to refresh; the question is only whether
        // it has been logged in at all.
        Provider::Copilot => gh_hosts_paths().iter().any(|path| path.exists()),
        _ => true,
    }
}

/// Where `gh` records a completed login. Windows uses `%AppData%`, everything
/// else uses `~/.config`.
fn gh_hosts_paths() -> Vec<PathBuf> {
    let mut paths = vec![home_dir().join(".config").join("gh").join("hosts.yml")];
    if let Some(appdata) = std::env::var_os("AppData").filter(|v| !v.is_empty()) {
        paths.push(PathBuf::from(appdata).join("GitHub CLI").join("hosts.yml"));
    }
    paths
}

/// One command that refreshes a provider's token as a side effect.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Probe {
    pub cli: &'static str,
    pub args: &'static [&'static str],
}

/// Commands that refresh an expired token, in the order they are tried.
///
/// The distinction that matters: a command must call an *authenticated
/// endpoint*. `--version`, `auth status`, and similar only read local state, so
/// running one leaves an expired token expired and the whole refresh silently
/// does nothing. Every entry here reaches the network.
pub fn probes(provider: Provider) -> &'static [Probe] {
    match provider {
        Provider::Grok => &[Probe {
            cli: "grok",
            args: &["models"],
        }],
        Provider::Antigravity => &[
            Probe {
                cli: "agy",
                args: &["models"],
            },
            Probe {
                cli: "antigravity",
                args: &["models"],
            },
        ],
        Provider::Claude => &[
            // `doctor` resolves the account, which makes Claude Code refresh
            // the OAuth token in ~/.claude/.credentials.json. `mcp list`
            // refreshes the same way and serves as a fallback.
            Probe {
                cli: "claude",
                args: &["doctor"],
            },
            Probe {
                cli: "claude",
                args: &["mcp", "list"],
            },
        ],
        Provider::Gemini => &[
            Probe {
                cli: "gemini",
                args: &["--version"],
            },
            Probe {
                cli: "gcloud",
                args: &["auth", "print-access-token"],
            },
        ],
        Provider::Copilot => &[
            Probe {
                cli: "copilot",
                args: &["--version"],
            },
            Probe {
                cli: "gh",
                args: &["auth", "token"],
            },
        ],
        _ => &[],
    }
}

/// Whether a command can be found on `PATH`.
pub fn is_cli_available(name: &str) -> bool {
    let name = name.trim();
    if name.is_empty() {
        return false;
    }
    let path = Path::new(name);
    if path.is_absolute() {
        return path.exists();
    }

    let Some(search) = std::env::var_os("PATH") else {
        return false;
    };
    let extensions = executable_extensions();

    std::env::split_paths(&search).any(|dir| {
        extensions
            .iter()
            .any(|extension| dir.join(format!("{name}{extension}")).is_file())
    })
}

#[cfg(windows)]
fn executable_extensions() -> Vec<String> {
    std::env::var("PATHEXT")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.split(';').map(str::to_string).collect())
        .unwrap_or_else(|| {
            [".exe", ".cmd", ".bat", ".com"]
                .iter()
                .map(|s| s.to_string())
                .collect()
        })
}

#[cfg(not(windows))]
fn executable_extensions() -> Vec<String> {
    vec![String::new()]
}

/// Run a probe, killing it if it outlives [`PROBE_TIMEOUT`].
///
/// The exit status is not the answer. A probe can succeed and still not have
/// refreshed anything, so the caller re-reads the token store instead.
fn run_probe(probe: &Probe) -> bool {
    let mut command = Command::new(probe.cli);
    command.args(probe.args);
    headless::prepare(&mut command);
    // Discarded rather than piped: these CLIs are chatty, and a full pipe
    // nobody is draining would block the child until the timeout kills it.
    command.stdout(Stdio::null());
    command.stderr(Stdio::null());

    let Ok(mut child) = command.spawn() else {
        return false;
    };

    // std has no wait-with-timeout. Polling keeps the child handle in this
    // thread, which is what makes the kill on timeout possible at all.
    let deadline = Instant::now() + PROBE_TIMEOUT;
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return status.success(),
            Ok(None) => {}
            Err(_) => return false,
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return false;
        }
        std::thread::sleep(PROBE_POLL);
    }
}

/// Ask a provider's own CLI to refresh its token, trying each probe until the
/// stored credential reads as usable again.
pub fn force_refresh(provider: Provider) -> bool {
    for probe in probes(provider) {
        if !is_cli_available(probe.cli) {
            continue;
        }
        run_probe(probe);
        if is_token_working(provider) {
            return true;
        }
    }
    false
}

/// Make sure a provider's credential is usable, refreshing it if it is not.
pub fn ensure_ready(provider: Provider) -> bool {
    is_token_working(provider) || force_refresh(provider)
}

/// The GitHub token `gh` holds, for Copilot quota reads.
pub fn github_token() -> Option<String> {
    let mut command = Command::new("gh");
    command.args(["auth", "token"]);
    headless::prepare(&mut command);
    let output = command.output().ok()?;
    if !output.status.success() {
        return None;
    }
    let token = String::from_utf8_lossy(&output.stdout).trim().to_string();
    (!token.is_empty()).then_some(token)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn decodes_base64_in_both_alphabets_and_without_padding() {
        assert_eq!(base64_decode("aGVsbG8=").unwrap(), b"hello");
        assert_eq!(base64_decode("aGVsbG8").unwrap(), b"hello");
        // URL-safe input containing bytes that encode to '-' and '_'.
        assert_eq!(base64_decode("--__").unwrap(), vec![251, 239, 255]);
        assert!(base64_decode("not valid!").is_none());
    }

    #[test]
    fn reads_the_email_claim_from_a_jwt() {
        // {"email":"user@example.com"} in URL-safe base64, unpadded.
        let payload = "eyJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ";
        assert_eq!(
            email_from_jwt(&format!("header.{payload}.signature")),
            "user@example.com"
        );
    }

    #[test]
    fn reads_the_openai_profile_email_claim() {
        // {"https://api.openai.com/profile":{"email":"a@b.com"}}
        let payload = "eyJodHRwczovL2FwaS5vcGVuYWkuY29tL3Byb2ZpbGUiOnsiZW1haWwiOiJhQGIuY29tIn19";
        assert_eq!(email_from_jwt(&format!("h.{payload}.s")), "a@b.com");
    }

    #[test]
    fn malformed_jwts_yield_no_email_rather_than_panicking() {
        for bad in ["", "single", "a.!!!.c", "a.YWJj.c"] {
            assert_eq!(email_from_jwt(bad), "");
        }
    }

    #[test]
    fn parse_grok_oauth_payload() {
        let payload = json!({
            "access_token": "xai-oauth-access-token",
            "refresh_token": "xai-refresh-token",
            "token_type": "Bearer",
            "expires_at": 1786741307,
            "scopes": ["openid", "profile", "email"]
        });
        let token = parse_grok(&payload).unwrap();
        assert_eq!(token.access_token, "xai-oauth-access-token");
        assert_eq!(token.refresh_token, "xai-refresh-token");
        assert_eq!(token.expires_at, Some(1786741307));
    }

    #[test]
    fn parse_grok_cli_auth_json_map() {
        let payload = json!({
            "account1": {
                "key": "xai-api-key-test",
                "email": "grokuser@example.com",
                "expires_at": 1786741307,
                "auth_mode": "oidc"
            }
        });
        let token = parse_grok(&payload).unwrap();
        assert_eq!(token.access_token, "xai-api-key-test");
        assert_eq!(token.email, "grokuser@example.com");
        assert_eq!(token.auth_method, "oidc");
    }

    #[test]
    fn a_stale_keyring_entry_never_shadows_a_live_credential() {
        let live = now_unix() + 3_600;
        let stale = Token {
            access_token: "expired-keyring-copy".into(),
            expires_at: Some(now_unix() - 3_600),
            ..Default::default()
        };
        let fresh = Token {
            access_token: "refreshed-cli-token".into(),
            expires_at: Some(live),
            ..Default::default()
        };

        // Canonical store first but expired, keyring second and live.
        let picked = pick_live(vec![stale.clone(), fresh.clone()]).unwrap();
        assert_eq!(picked.access_token, "refreshed-cli-token");

        // Live canonical store wins over anything behind it.
        let picked = pick_live(vec![fresh.clone(), stale.clone()]).unwrap();
        assert_eq!(picked.access_token, "refreshed-cli-token");
    }

    #[test]
    fn all_expired_falls_back_to_the_canonical_store_for_the_probe() {
        let older = Token {
            access_token: "canonical".into(),
            expires_at: Some(now_unix() - 7_200),
            ..Default::default()
        };
        let newer = Token {
            access_token: "keyring".into(),
            expires_at: Some(now_unix() - 60),
            ..Default::default()
        };
        assert_eq!(
            pick_live(vec![older, newer]).unwrap().access_token,
            "canonical"
        );
        assert!(pick_live(Vec::new()).is_none());
    }

    #[test]
    fn parse_claude_oauth_direct_and_nested() {
        let nested = json!({
            "claudeAiOauth": {
                "accessToken": "claude-nested-token",
                "refreshToken": "claude-refresh",
                "expiresAt": "2026-08-14T21:01:47Z",
                "email": "user@claude.ai",
                "subscriptionType": "pro"
            }
        });
        let tok1 = parse_claude(&nested).unwrap();
        assert_eq!(tok1.access_token, "claude-nested-token");
        assert_eq!(tok1.email, "user@claude.ai");

        let direct = json!({
            "access_token": "claude-direct-token",
            "refresh_token": "claude-direct-refresh",
            "expires_at": 1786741307,
            "subscriptionType": "max"
        });
        let tok2 = parse_claude(&direct).unwrap();
        assert_eq!(tok2.access_token, "claude-direct-token");
        assert_eq!(tok2.plan_type, "max");
    }

    #[test]
    fn a_token_without_an_expiry_is_treated_as_fresh() {
        let token = Token {
            access_token: "t".into(),
            ..Default::default()
        };
        assert!(token.is_fresh());
    }

    #[test]
    fn expiry_inside_the_margin_counts_as_spent() {
        let nearly = Token {
            access_token: "t".into(),
            expires_at: Some(now_unix() + EXPIRY_MARGIN - 1),
            ..Default::default()
        };
        assert!(!nearly.is_fresh());

        let good = Token {
            access_token: "t".into(),
            expires_at: Some(now_unix() + 3600),
            ..Default::default()
        };
        assert!(good.is_fresh());
    }

    #[test]
    fn an_empty_access_token_is_never_fresh() {
        assert!(!Token::default().is_fresh());
    }

    #[test]
    fn expiry_is_read_from_strings_seconds_and_milliseconds() {
        assert_eq!(
            expiry_at(
                &json!({"expiresAt": "2026-08-14T21:01:47Z"}),
                &["expiresAt"]
            ),
            Some(1_786_741_307)
        );
        assert_eq!(
            expiry_at(&json!({"expiresAt": 1_700_000_000}), &["expiresAt"]),
            Some(1_700_000_000)
        );
        assert_eq!(
            expiry_at(&json!({"expiresAt": 1_700_000_000_000_i64}), &["expiresAt"]),
            Some(1_700_000_000)
        );
        assert_eq!(expiry_at(&json!({}), &["expiresAt"]), None);
    }

    #[test]
    fn antigravity_token_falls_back_to_the_id_token_for_an_email() {
        let payload = "eyJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ";
        let token = parse_antigravity(&json!({
            "auth_method": "oauth",
            "id_token": format!("h.{payload}.s"),
            "token": {"access_token": "ya29.token", "expiry": "2026-08-14T21:01:47Z"}
        }))
        .unwrap();

        assert_eq!(token.access_token, "ya29.token");
        assert_eq!(token.email, "user@example.com");
        assert_eq!(token.auth_method, "oauth");
        assert_eq!(token.expires_at, Some(1_786_741_307));
    }

    #[test]
    fn antigravity_payload_without_a_token_object_is_rejected() {
        assert!(parse_antigravity(&json!({"auth_method": "oauth"})).is_none());
        assert!(parse_antigravity(&json!({"token": {"access_token": "  "}})).is_none());
    }

    #[test]
    fn claude_probes_call_an_authenticated_endpoint() {
        let claude = probes(Provider::Claude);
        assert!(claude.contains(&Probe {
            cli: "claude",
            args: &["doctor"]
        }));
        // Local-only commands cannot refresh anything; listing one here would
        // make the whole refresh path silently useless.
        for dead in [
            &["auth", "status"][..],
            &["--version"][..],
            &["agents", "--json"][..],
        ] {
            assert!(
                !claude.iter().any(|p| p.args == dead),
                "{dead:?} cannot refresh an expired token"
            );
        }
    }

    #[test]
    fn grok_probe_is_the_authenticated_models_call() {
        assert_eq!(
            probes(Provider::Grok),
            &[Probe {
                cli: "grok",
                args: &["models"]
            }]
        );
    }

    #[test]
    fn providers_without_a_local_store_need_no_probe() {
        assert!(probes(Provider::OpenCode).is_empty());
        assert!(probes(Provider::OpenAi).is_empty());
        assert!(is_token_working(Provider::OpenCode));
    }

    #[test]
    fn missing_commands_are_not_reported_as_available() {
        assert!(!is_cli_available(""));
        assert!(!is_cli_available("   "));
        assert!(!is_cli_available("definitely-not-a-real-command-xyzzy"));
    }
}
