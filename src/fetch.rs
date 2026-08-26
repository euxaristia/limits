//! Reading each provider's quota.
//!
//! Every fetch goes through the injected [`HttpClient`], so the whole module is
//! testable against canned responses and an embedder can supply its own
//! transport. Nothing here panics or propagates an error upward: a provider
//! that cannot be read becomes a degraded or unconfigured [`ProviderUsage`], so
//! one dead endpoint never takes the report down with it.

use crate::credentials::{self, Token};
use crate::http::{HttpClient, HttpRequest, HttpResponse};
use crate::model::{Provider, ProviderConfig, ProviderUsage, UsageWindow};
use crate::parsers::{antigravity, claude, codex, copilot, opencode};
use crate::time::{WEEK, format_countdown, now_unix};
use serde_json::Value;

/// Sent on requests that are not impersonating a specific CLI. OpenCode Go sits
/// behind Cloudflare, which rejects some non-browser signatures outright.
const BROWSER_UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

/// Reads provider quotas over a given transport.
pub struct Fetcher<'a> {
    http: &'a dyn HttpClient,
}

impl<'a> Fetcher<'a> {
    pub fn new(http: &'a dyn HttpClient) -> Self {
        Fetcher { http }
    }

    /// Read one provider, resolving credentials as needed.
    pub fn fetch(&self, config: &ProviderConfig) -> ProviderUsage {
        let provider = config.provider();
        match provider {
            Provider::Codex => self.codex(),
            Provider::Claude => self.claude(config),
            Provider::Gemini => self.gemini(),
            Provider::Antigravity => self.antigravity(),
            Provider::Grok => self.grok(config),
            Provider::Copilot => self.copilot(config),
            Provider::OpenCode => self.opencode(config),
            Provider::OpenAi if config.has_api_key() => self.openai(&config.api_key),
            Provider::DeepSeek if config.has_api_key() => self.deepseek(&config.api_key),
            Provider::OpenRouter if config.has_api_key() => self.openrouter(&config.api_key),
            _ => ProviderUsage::unconfigured(provider),
        }
    }

    /// Send a request, reducing a transport failure to its message.
    fn send(&self, request: HttpRequest) -> Result<HttpResponse, String> {
        self.http.send(&request).map_err(|e| e.to_string())
    }

    /// Send and require a 2xx, naming the endpoint when it is not.
    fn send_ok(&self, what: &str, request: HttpRequest) -> Result<HttpResponse, String> {
        let response = self.send(request)?;
        if response.is_success() {
            return Ok(response);
        }
        Err(format!("{what} returned HTTP {}", response.status))
    }

    /// A best-effort GET whose failure is not worth reporting: these only add
    /// an account label to a footer.
    fn probe_json(&self, request: HttpRequest) -> Option<Value> {
        let response = self.http.send(&request).ok()?;
        response
            .is_success()
            .then(|| response.json().ok())
            .flatten()
    }

    // ---- ChatGPT Codex ---------------------------------------------------

    fn codex(&self) -> ProviderUsage {
        let Some(token) = credentials::load_codex().filter(Token::is_usable) else {
            return ProviderUsage::unconfigured(Provider::Codex);
        };

        let request = HttpRequest::get("https://chatgpt.com/backend-api/wham/usage")
            .bearer(&token.access_token)
            .header("User-Agent", "codex-cli")
            .header("Accept", "application/json")
            .optional_header("ChatGPT-Account-Id", &token.account_id);

        let response = match self.send(request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Codex, message),
        };
        if !response.is_success() {
            return ProviderUsage::degraded(
                Provider::Codex,
                format!(
                    "Codex usage API returned HTTP {}; run 'codex login' if your session expired",
                    response.status
                ),
            );
        }

        let usage = match codex::parse(&response.body, now_unix()) {
            Ok(usage) => usage,
            Err(message) => return ProviderUsage::degraded(Provider::Codex, message),
        };

        let plan = first_non_empty(&[&usage.plan_type, &token.plan_type]);
        let mut footer = "ChatGPT".to_string();
        if !plan.is_empty() {
            footer.push(' ');
            footer.push_str(&plan);
        }
        if !token.email.is_empty() {
            footer = format!("{footer} ({})", token.email);
        }
        ProviderUsage::healthy(Provider::Codex, usage.windows, footer)
    }

    // ---- Claude ----------------------------------------------------------

    fn claude(&self, config: &ProviderConfig) -> ProviderUsage {
        // An explicit cookie or key means the user wants the web account read,
        // not whatever the local CLI happens to be signed in as.
        if config.has_cookie() || config.has_api_key() {
            let source = if config.has_cookie() {
                &config.cookie_header
            } else {
                &config.api_key
            };
            return self.claude_web(source);
        }

        credentials::ensure_ready(Provider::Claude);
        let Some(token) = credentials::load_claude().filter(Token::is_usable) else {
            return ProviderUsage::unconfigured(Provider::Claude);
        };

        let usage = self.claude_oauth(&token);
        if usage.status != crate::model::Status::Degraded {
            return usage;
        }
        // A rejected token is the one failure a refresh can fix, so it is worth
        // one more round trip.
        if !usage.error_message.contains("401") && !usage.error_message.contains("403") {
            return usage;
        }
        if !credentials::force_refresh(Provider::Claude) {
            return usage;
        }
        match credentials::load_claude().filter(Token::is_fresh) {
            Some(fresh) => self.claude_oauth(&fresh),
            None => usage,
        }
    }

    fn claude_oauth(&self, token: &Token) -> ProviderUsage {
        let request = HttpRequest::get("https://api.anthropic.com/api/oauth/usage")
            .bearer(&token.access_token)
            .header("anthropic-beta", "oauth-2025-04-20")
            .header("Accept", "application/json");

        let response = match self.send_ok("Claude OAuth API", request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Claude, message),
        };

        let mut email = token.email.clone();
        if email.is_empty() {
            email = self.claude_profile_email(&token.access_token);
        }
        let footer = account_footer("Claude CLI", &[&email, &title_case(&token.plan_type)]);
        ProviderUsage::healthy(Provider::Claude, claude::parse(&response.body), footer)
    }

    fn claude_web(&self, cookie_source: &str) -> ProviderUsage {
        let session_key = session_key_from(cookie_source);
        let cookie = format!("sessionKey={session_key}");

        let organizations = match self.send_ok(
            "Claude organizations API",
            HttpRequest::get("https://claude.ai/api/organizations")
                .header("Cookie", cookie.clone())
                .header("User-Agent", BROWSER_UA)
                .header("Accept", "application/json"),
        ) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Claude, message),
        };

        let organization_id = organizations
            .json()
            .ok()
            .as_ref()
            .and_then(Value::as_array)
            .and_then(|orgs| orgs.first())
            .and_then(|org| org.get("uuid"))
            .and_then(Value::as_str)
            .map(str::to_string);
        let Some(organization_id) = organization_id else {
            return ProviderUsage::degraded(Provider::Claude, "No organization UUID found");
        };

        let usage = match self.send_ok(
            "Claude usage API",
            HttpRequest::get(format!(
                "https://claude.ai/api/organizations/{organization_id}/usage"
            ))
            .header("Cookie", cookie)
            .header("User-Agent", BROWSER_UA)
            .header("Accept", "application/json"),
        ) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Claude, message),
        };

        ProviderUsage::healthy(Provider::Claude, claude::parse(&usage.body), "Claude Web")
    }

    fn claude_profile_email(&self, access_token: &str) -> String {
        if access_token.trim().is_empty() {
            return String::new();
        }
        self.probe_json(
            HttpRequest::get("https://api.anthropic.com/api/oauth/profile")
                .bearer(access_token)
                .header("anthropic-beta", "oauth-2025-04-20"),
        )
        .and_then(|body| {
            body.get("account")?
                .get("email")?
                .as_str()
                .map(str::to_string)
        })
        .unwrap_or_default()
    }

    // ---- OpenCode Go -----------------------------------------------------

    fn opencode(&self, config: &ProviderConfig) -> ProviderUsage {
        let Some(key) = opencode_key(config) else {
            return ProviderUsage::unconfigured(Provider::OpenCode);
        };

        let request = HttpRequest::get("https://opencode.ai/zen/go/v1/usage")
            .bearer(&key)
            .header("User-Agent", BROWSER_UA)
            .header("Accept", "application/json");

        let response = match self.send(request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::OpenCode, message),
        };
        if response.is_auth_failure() {
            return ProviderUsage::degraded(
                Provider::OpenCode,
                "OpenCode Go rejected the key; re-authenticate the CLI or set OPENCODE_GO_API_KEY",
            );
        }
        if !response.is_success() {
            return ProviderUsage::degraded(
                Provider::OpenCode,
                format!("OpenCode usage API returned HTTP {}", response.status),
            );
        }

        let windows = opencode::parse(&response.body);
        if windows.is_empty() {
            return ProviderUsage::degraded(
                Provider::OpenCode,
                "OpenCode usage API reported no windows",
            );
        }

        let spent: Vec<&str> = windows
            .iter()
            .filter(|w| w.is_spent())
            .map(|w| w.label.as_str())
            .collect();
        let footer = if spent.is_empty() {
            "OpenCode Go subscription".to_string()
        } else {
            format!("OpenCode Go subscription - {} spent", spent.join(", "))
        };
        ProviderUsage::healthy(Provider::OpenCode, windows, footer)
    }

    // ---- Balance-style providers -----------------------------------------

    fn openai(&self, api_key: &str) -> ProviderUsage {
        let request = HttpRequest::get("https://api.openai.com/v1/dashboard/billing/credit_grants")
            .bearer(api_key)
            .header("User-Agent", BROWSER_UA)
            .header("Accept", "application/json");

        let response = match self.send_ok("OpenAI billing API", request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::OpenAi, message),
        };
        let Ok(body) = response.json() else {
            return ProviderUsage::degraded(Provider::OpenAi, "unexpected OpenAI response shape");
        };

        let granted = number_at(&body, "total_granted");
        let used = number_at(&body, "total_used");
        let available = number_at(&body, "total_available");
        ProviderUsage::from_balance(
            Provider::OpenAi,
            used,
            granted,
            "Credit Expiry",
            format!("Available: ${available:.2} / ${granted:.2}"),
        )
    }

    fn deepseek(&self, api_key: &str) -> ProviderUsage {
        let request = HttpRequest::get("https://api.deepseek.com/user/balance")
            .bearer(api_key)
            .header("User-Agent", BROWSER_UA)
            .header("Accept", "application/json");

        let response = match self.send_ok("DeepSeek balance API", request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::DeepSeek, message),
        };
        let Ok(body) = response.json() else {
            return ProviderUsage::degraded(
                Provider::DeepSeek,
                "unexpected DeepSeek response shape",
            );
        };
        if !body
            .get("is_available")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            return ProviderUsage::degraded(Provider::DeepSeek, "Account is unavailable");
        }

        // Balances arrive as decimal strings so they do not lose cents to a
        // float in transit.
        let balance: f64 = body
            .get("balance_infos")
            .and_then(Value::as_array)
            .map(|infos| {
                infos
                    .iter()
                    .filter_map(|info| info.get("balance")?.as_str()?.parse::<f64>().ok())
                    .sum()
            })
            .unwrap_or(0.0);

        // A prepaid balance has no denominator, so there is no percentage to
        // show; the footer carries the number that matters.
        ProviderUsage::from_balance(
            Provider::DeepSeek,
            0.0,
            balance,
            "Never Resets",
            format!("Available: ${balance:.2}"),
        )
    }

    fn openrouter(&self, api_key: &str) -> ProviderUsage {
        let request = HttpRequest::get("https://openrouter.ai/api/v1/auth/key")
            .bearer(api_key)
            .header("User-Agent", BROWSER_UA)
            .header("Accept", "application/json");

        let response = match self.send_ok("OpenRouter key API", request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::OpenRouter, message),
        };
        let Some(data) = response
            .json()
            .ok()
            .and_then(|body| body.get("data").cloned())
        else {
            return ProviderUsage::degraded(
                Provider::OpenRouter,
                "unexpected OpenRouter response shape",
            );
        };

        let limit = number_at(&data, "limit");
        let used = number_at(&data, "usage");
        ProviderUsage::from_balance(
            Provider::OpenRouter,
            used,
            limit,
            "Monthly Reset",
            format!("Used: ${used:.2} / ${limit:.2}"),
        )
    }

    // ---- Google -----------------------------------------------------------

    fn gemini(&self) -> ProviderUsage {
        credentials::ensure_ready(Provider::Gemini);
        let Some(token) = credentials::load_gemini().filter(Token::is_usable) else {
            return ProviderUsage::unconfigured(Provider::Gemini);
        };

        let request = HttpRequest::post(
            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota",
            "{}",
        )
        .bearer(&token.access_token)
        .header("Content-Type", "application/json")
        .header("User-Agent", BROWSER_UA);

        let response = match self.send_ok("Gemini quota API", request) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Gemini, message),
        };
        let Ok(body) = response.json() else {
            return ProviderUsage::degraded(Provider::Gemini, "unexpected Gemini response shape");
        };

        // The account is as limited as its most depleted bucket.
        let mut lowest = 1.0_f64;
        let mut reset = "Daily Quota".to_string();
        if let Some(buckets) = body.get("buckets").and_then(Value::as_array) {
            for bucket in buckets {
                if let Some(fraction) = bucket.get("remainingFraction").and_then(Value::as_f64) {
                    lowest = lowest.min(fraction);
                }
                if let Some(at) = bucket.get("resetTime").and_then(Value::as_str)
                    && !at.is_empty()
                {
                    reset = format_countdown(at);
                }
            }
        }

        ProviderUsage::healthy(
            Provider::Gemini,
            vec![UsageWindow::new("Quota", (1.0 - lowest) * 100.0).reset(reset)],
            "Google Code Assist Quota",
        )
    }

    fn antigravity(&self) -> ProviderUsage {
        credentials::ensure_ready(Provider::Antigravity);
        let Some(token) = credentials::load_antigravity().filter(Token::is_usable) else {
            return ProviderUsage::unconfigured(Provider::Antigravity);
        };

        let quota = |endpoint: &str| {
            HttpRequest::post(endpoint, "{}")
                .bearer(&token.access_token)
                .header("Content-Type", "application/json")
                .header("User-Agent", "antigravity")
        };

        // The summary endpoint is the current one; the older per-model endpoint
        // is the fallback for builds that do not serve it yet.
        let mut response = self.http.send(&quota(
            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
        ));
        if !matches!(&response, Ok(r) if r.is_success()) {
            response = self.http.send(&quota(
                "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota",
            ));
        }

        let response = match response {
            Ok(response) => response,
            Err(e) => return ProviderUsage::degraded(Provider::Antigravity, e.to_string()),
        };
        if !response.is_success() {
            return ProviderUsage::degraded(
                Provider::Antigravity,
                truncate(
                    &format!(
                        "Antigravity API returned status {}: {}",
                        response.status, response.body
                    ),
                    200,
                ),
            );
        }

        let mut email = token.email.clone();
        if email.is_empty() {
            email = self.google_email(&token.access_token);
        }
        let account = if email.is_empty() {
            token.auth_method.clone()
        } else {
            format!("{email}, {}", token.auth_method)
        };

        let buckets = antigravity::parse(&response.body);
        if buckets.is_empty() {
            return ProviderUsage::healthy(
                Provider::Antigravity,
                vec![UsageWindow::new("Quota", 0.0).reset("N/A")],
                format!("Antigravity ({account}) - no quota data"),
            );
        }

        let mut models: Vec<&str> = buckets
            .iter()
            .flat_map(|b| b.members.iter().map(String::as_str))
            .collect();
        models.sort_unstable();
        models.dedup();

        let windows = buckets
            .iter()
            .map(|bucket| {
                let timeframe = match bucket.timeframe {
                    antigravity::Timeframe::Quota => String::new(),
                    other => format!("{} ", other.label()),
                };
                let label = format!(
                    "{timeframe}{} ({})",
                    bucket.group_label,
                    plural(bucket.members.len(), "model")
                );
                // Below 10% the rounded integer reads as a cliff edge ("0%
                // remaining" with requests still going through), so the last
                // stretch keeps a decimal.
                let remaining = if bucket.remaining_percent < 10.0 {
                    format!("{:.1}% remaining", bucket.remaining_percent)
                } else {
                    format!("{:.0}% remaining", bucket.remaining_percent)
                };
                UsageWindow::new(label, bucket.used_percent)
                    .reset(bucket.reset_countdown.clone())
                    .text(remaining)
            })
            .collect();

        ProviderUsage::healthy(
            Provider::Antigravity,
            windows,
            format!(
                "Antigravity ({account}) - {}, {}",
                plural(buckets.len(), "group"),
                plural(models.len(), "model")
            ),
        )
    }

    fn google_email(&self, access_token: &str) -> String {
        self.probe_json(
            HttpRequest::get("https://www.googleapis.com/oauth2/v1/userinfo").bearer(access_token),
        )
        .and_then(|body| body.get("email")?.as_str().map(str::to_string))
        .unwrap_or_default()
    }

    // ---- Grok -------------------------------------------------------------

    fn grok(&self, config: &ProviderConfig) -> ProviderUsage {
        credentials::ensure_ready(Provider::Grok);
        let token = credentials::load_grok();
        let bearer = match &token {
            Some(token) if token.is_usable() => token.access_token.clone(),
            _ => config.api_key.trim().to_string(),
        };
        if bearer.is_empty() {
            return ProviderUsage::unconfigured(Provider::Grok);
        }

        let grok_request = |url: &str| {
            HttpRequest::get(url)
                .bearer(&bearer)
                .header("User-Agent", "grok/0.2.111")
                .header("x-grok-client-version", "0.2.111")
                .header("Accept", "application/json")
        };

        let user = match self.send_ok(
            "Grok user API",
            grok_request("https://cli-chat-proxy.grok.com/v1/user"),
        ) {
            Ok(response) => response,
            Err(message) => return ProviderUsage::degraded(Provider::Grok, message),
        };

        let email = match token.as_ref().map(|t| t.email.clone()) {
            Some(email) if !email.is_empty() => email,
            _ => user
                .json()
                .ok()
                .and_then(|body| body.get("email")?.as_str().map(str::to_string))
                .unwrap_or_default(),
        };

        // Billing is where the percentage lives; the session expiry is only a
        // stand-in until it answers. `None` means it never answered, which is
        // not the same as a window that has gone unspent.
        let mut used_percent = None;
        let mut reset = match token.as_ref().and_then(|t| t.expires_at) {
            Some(expiry) => crate::time::countdown_between(expiry, now_unix()),
            None => "Active".to_string(),
        };

        if let Some(billing) = self.probe_json(grok_request(
            "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
        )) && let Some(config) = billing.get("config")
        {
            used_percent = Some(grok_used_percent(config));
            if let Some(end) = config
                .get("currentPeriod")
                .and_then(|period| period.get("end"))
                .and_then(Value::as_str)
                .filter(|end| !end.is_empty())
            {
                reset = format_countdown(end);
            }
        }

        let account = if email.is_empty() { "Active" } else { &email };
        let window = match used_percent {
            Some(percent) => {
                UsageWindow::new("Weekly", percent).text(format!("{percent:.0}% used"))
            }
            None => UsageWindow::new("Weekly", 0.0).text("usage unavailable"),
        };
        ProviderUsage::healthy(
            Provider::Grok,
            vec![window.reset(reset).seconds(WEEK)],
            format!("Grok CLI ({account})"),
        )
    }

    // ---- GitHub Copilot ---------------------------------------------------

    fn copilot(&self, config: &ProviderConfig) -> ProviderUsage {
        let bearer = match config.has_api_key() {
            true => config.api_key.trim().to_string(),
            false => credentials::github_token().unwrap_or_default(),
        };
        if bearer.is_empty() {
            return ProviderUsage::unconfigured(Provider::Copilot);
        }

        let response = match self.send(
            HttpRequest::get("https://api.github.com/copilot_internal/user")
                .bearer(&bearer)
                .header("User-Agent", BROWSER_UA)
                .header("Accept", "application/json"),
        ) {
            Ok(response) => response,
            Err(message) => return copilot_unavailable(&message),
        };
        if !response.is_success() {
            return copilot_unavailable(&format!("GitHub returned {}", response.status));
        }

        let payload = match copilot::parse(&response.body) {
            Ok(payload) => payload,
            Err(reason) => return copilot_unavailable(&reason),
        };
        let (windows, exhausted) = copilot::windows(&payload);
        if windows.is_empty() {
            return copilot_unavailable("no quotas reported for this account");
        }

        let email = self.github_email(&bearer);
        let user = match (payload.login.as_str(), email.as_str()) {
            ("", "") => "user".to_string(),
            (login, "") => login.to_string(),
            ("", email) => email.to_string(),
            (login, email) => format!("{login}, {email}"),
        };
        let plan = payload.plan_label();
        let mut footer = if plan.is_empty() {
            format!("GitHub Copilot (User: {user})")
        } else {
            format!("GitHub Copilot (User: {user}, Plan: {plan})")
        };
        if exhausted {
            footer.push_str(" - Quota Exceeded");
        }

        ProviderUsage::healthy(Provider::Copilot, windows, footer)
    }

    fn github_email(&self, bearer: &str) -> String {
        self.probe_json(
            HttpRequest::get("https://api.github.com/user")
                .bearer(bearer)
                .header("User-Agent", BROWSER_UA),
        )
        .and_then(|body| body.get("email")?.as_str().map(str::to_string))
        .unwrap_or_default()
    }
}

fn copilot_unavailable(reason: &str) -> ProviderUsage {
    // The endpoint is undocumented and can change without notice, so an
    // unreadable response is reported as such rather than as a healthy zero.
    ProviderUsage::degraded(
        Provider::Copilot,
        format!("Copilot quota unavailable: {reason}"),
    )
}

/// The OpenCode Go key: explicit config first, then the two environment
/// variables in use (`OPENCODE_GO_API_KEY` is the documented one;
/// `OPENCODE_API_KEY` is what the upstream CLI sets), then the stores the
/// local CLIs already keep it in.
fn opencode_key(config: &ProviderConfig) -> Option<String> {
    if config.has_api_key() {
        return Some(config.api_key.trim().to_string());
    }
    ["OPENCODE_GO_API_KEY", "OPENCODE_API_KEY"]
        .iter()
        .find_map(|name| {
            std::env::var(name)
                .ok()
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
        })
        .or_else(credentials::load_opencode_key)
}

/// Pull the session key out of either a bare key or a full Cookie header.
fn session_key_from(source: &str) -> String {
    match source.split_once("sessionKey=") {
        Some((_, rest)) => rest.split(';').next().unwrap_or(rest).trim().to_string(),
        None => source.trim().to_string(),
    }
}

fn number_at(value: &Value, key: &str) -> f64 {
    value.get(key).and_then(Value::as_f64).unwrap_or(0.0)
}

fn first_non_empty(candidates: &[&str]) -> String {
    candidates
        .iter()
        .map(|s| s.trim())
        .find(|s| !s.is_empty())
        .unwrap_or_default()
        .to_string()
}

/// `Claude CLI (a@b.com, Max)`, skipping whichever parts are unknown.
fn account_footer(base: &str, parts: &[&str]) -> String {
    let known: Vec<&str> = parts.iter().copied().filter(|p| !p.is_empty()).collect();
    if known.is_empty() {
        base.to_string()
    } else {
        format!("{base} ({})", known.join(", "))
    }
}

fn plural(count: usize, noun: &str) -> String {
    if count == 1 {
        format!("{count} {noun}")
    } else {
        format!("{count} {noun}s")
    }
}

fn title_case(value: &str) -> String {
    let mut chars = value.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().collect::<String>() + chars.as_str(),
        None => String::new(),
    }
}

fn truncate(value: &str, max: usize) -> String {
    if value.chars().count() <= max {
        return value.to_string();
    }
    format!("{}...", value.chars().take(max).collect::<String>())
}

/// Read every enabled provider at once.
///
/// One thread per provider, joined before returning. These are all blocking
/// network calls with nothing to compute, so the wall time is the slowest
/// provider rather than the sum, and no async runtime is needed to get that.
pub fn fetch_all(http: &dyn HttpClient, configs: &[ProviderConfig]) -> Vec<ProviderUsage> {
    std::thread::scope(|scope| {
        let handles: Vec<_> = configs
            .iter()
            .map(|config| scope.spawn(move || Fetcher::new(http).fetch(config)))
            .collect();

        handles
            .into_iter()
            .zip(configs)
            .map(|(handle, config)| {
                handle.join().unwrap_or_else(|_| {
                    ProviderUsage::degraded(config.provider(), "provider read panicked")
                })
            })
            .collect()
    })
}

/// Share of the Grok credit window already spent, from a billing `config`.
///
/// The payload is protobuf JSON, which omits any field still holding its
/// default. A billing period that has seen no spend therefore comes back with
/// no `creditUsagePercent` at all, and that absence is a real zero rather than
/// missing data: only a billing call that never landed leaves it unknown.
fn grok_used_percent(config: &Value) -> f64 {
    config
        .get("creditUsagePercent")
        .and_then(Value::as_f64)
        .unwrap_or(0.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::http::HttpError;
    use std::sync::Mutex;

    /// Answers from a canned table, keyed by URL substring, and records what it
    /// was asked for.
    struct FakeHttp {
        routes: Vec<(&'static str, u16, String)>,
        seen: Mutex<Vec<HttpRequest>>,
    }

    impl FakeHttp {
        fn new(routes: Vec<(&'static str, u16, &str)>) -> Self {
            FakeHttp {
                routes: routes
                    .into_iter()
                    .map(|(url, status, body)| (url, status, body.to_string()))
                    .collect(),
                seen: Mutex::new(Vec::new()),
            }
        }

        fn header_for(&self, url_fragment: &str, name: &str) -> Option<String> {
            let seen = self.seen.lock().unwrap();
            let request = seen.iter().find(|r| r.url.contains(url_fragment))?;
            request
                .headers
                .iter()
                .find(|(key, _)| key.eq_ignore_ascii_case(name))
                .map(|(_, value)| value.clone())
        }

        fn request_count(&self) -> usize {
            self.seen.lock().unwrap().len()
        }
    }

    impl HttpClient for FakeHttp {
        fn send(&self, request: &HttpRequest) -> Result<HttpResponse, HttpError> {
            self.seen.lock().unwrap().push(request.clone());
            for (fragment, status, body) in &self.routes {
                if request.url.contains(fragment) {
                    return Ok(HttpResponse {
                        status: *status,
                        body: body.clone(),
                    });
                }
            }
            Err(HttpError(format!("no route for {}", request.url)))
        }
    }

    fn config(id: &str) -> ProviderConfig {
        ProviderConfig::new(id, true)
    }

    fn keyed(id: &str, key: &str) -> ProviderConfig {
        let mut config = ProviderConfig::new(id, true);
        config.api_key = key.into();
        config
    }

    const OPENCODE_USAGE: &str = r#"{"usage":{
        "rolling":{"status":"ok","percent":6,"resetsAt":"2099-08-14T21:01:47.112Z"},
        "weekly":{"status":"rate-limited","percent":100,"resetsAt":"2099-08-17T00:00:00.112Z"},
        "monthly":{"status":"ok","percent":50,"resetsAt":"2099-09-12T22:42:28.112Z"}
    }}"#;

    const GROK_USER: &str = r#"{"email":"grokuser@example.com"}"#;

    #[test]
    fn an_omitted_credit_percent_is_a_real_zero_not_missing_data() {
        // Protobuf JSON drops a field sitting at its default, so a billing
        // period with no spend answers without `creditUsagePercent` at all.
        let unspent = serde_json::json!({
            "currentPeriod": {"type": "USAGE_PERIOD_TYPE_WEEKLY"},
            "onDemandUsed": {"val": 0}
        });
        assert_eq!(grok_used_percent(&unspent), 0.0);

        let spent = serde_json::json!({"creditUsagePercent": 88.0});
        assert_eq!(grok_used_percent(&spent), 88.0);
    }

    #[test]
    fn grok_billing_that_never_answers_reports_unavailable_rather_than_zero() {
        // Only the user call is routed; billing gets no route and so errors.
        let http = FakeHttp::new(vec![("v1/user", 200, GROK_USER)]);
        let usage = Fetcher::new(&http).fetch(&keyed("grok", "xai-test-key"));

        assert_eq!(usage.status, crate::model::Status::Healthy);
        let window = &usage.windows[0];
        assert_eq!(window.label, "Weekly");
        assert_eq!(
            window.percent_text(),
            "usage unavailable",
            "a billing call that never landed must not read as 0% used"
        );
    }

    #[test]
    fn grok_reports_the_billing_percentage_when_it_answers() {
        let billing = r#"{"config":{"creditUsagePercent":88.0}}"#;
        let http = FakeHttp::new(vec![
            ("v1/billing", 200, billing),
            ("v1/user", 200, GROK_USER),
        ]);
        let usage = Fetcher::new(&http).fetch(&keyed("grok", "xai-test-key"));

        assert_eq!(usage.windows[0].percent_text(), "88% used");
        assert_eq!(usage.windows[0].used_percent, 88.0);
    }

    #[test]
    fn opencode_reports_all_three_windows_and_names_the_spent_one() {
        let http = FakeHttp::new(vec![("zen/go/v1/usage", 200, OPENCODE_USAGE)]);
        let usage = Fetcher::new(&http).fetch(&keyed("opencode", "sk-test"));

        assert_eq!(usage.status, crate::model::Status::Healthy);
        assert_eq!(usage.display_name, "OpenCode Go");
        assert_eq!(
            usage
                .windows
                .iter()
                .map(|w| w.label.as_str())
                .collect::<Vec<_>>(),
            ["Rolling", "Weekly", "Monthly"]
        );
        assert_eq!(usage.footer, "OpenCode Go subscription - Weekly spent");
        assert!(!usage.is_exhausted(), "rolling and monthly still have room");
    }

    #[test]
    fn opencode_sends_the_key_as_a_bearer_token() {
        let http = FakeHttp::new(vec![("zen/go/v1/usage", 200, OPENCODE_USAGE)]);
        Fetcher::new(&http).fetch(&keyed("opencode", "  sk-test  "));

        assert_eq!(
            http.header_for("zen/go", "Authorization").as_deref(),
            Some("Bearer sk-test")
        );
    }

    #[test]
    fn opencode_without_a_key_is_unconfigured_and_makes_no_request() {
        // The environment fallback must not leak a developer's real key into
        // this assertion, so the config path is what is exercised here.
        let http = FakeHttp::new(vec![]);
        let usage = Fetcher::new(&http).fetch(&keyed("opencode", ""));

        if opencode_key(&config("opencode")).is_none() {
            assert_eq!(usage.status, crate::model::Status::Unconfigured);
            assert_eq!(http.request_count(), 0);
        }
    }

    #[test]
    fn opencode_rejects_a_bad_key_with_an_actionable_message() {
        let http = FakeHttp::new(vec![("zen/go/v1/usage", 401, "unauthorized")]);
        let usage = Fetcher::new(&http).fetch(&keyed("opencode", "sk-bad"));

        assert_eq!(usage.status, crate::model::Status::Degraded);
        assert!(
            usage.error_message.contains("OPENCODE_GO_API_KEY"),
            "{}",
            usage.error_message
        );
    }

    #[test]
    fn opencode_reports_an_empty_payload_rather_than_a_healthy_zero() {
        let http = FakeHttp::new(vec![("zen/go/v1/usage", 200, "{}")]);
        let usage = Fetcher::new(&http).fetch(&keyed("opencode", "sk-test"));

        assert_eq!(usage.status, crate::model::Status::Degraded);
        assert!(usage.windows.is_empty());
    }

    #[test]
    fn a_transport_failure_degrades_one_provider_not_the_run() {
        let http = FakeHttp::new(vec![]);
        let usage = Fetcher::new(&http).fetch(&keyed("openrouter", "sk-or"));

        assert_eq!(usage.status, crate::model::Status::Degraded);
        assert!(usage.has_error);
    }

    #[test]
    fn openrouter_reports_spend_against_the_limit() {
        let http = FakeHttp::new(vec![(
            "openrouter.ai",
            200,
            r#"{"data":{"limit":100.0,"usage":25.0}}"#,
        )]);
        let usage = Fetcher::new(&http).fetch(&keyed("openrouter", "sk-or"));

        assert_eq!(usage.windows[0].used_percent, 25.0);
        assert_eq!(usage.footer, "Used: $25.00 / $100.00");
    }

    #[test]
    fn deepseek_sums_every_balance_it_reports() {
        let http = FakeHttp::new(vec![(
            "deepseek.com",
            200,
            r#"{"is_available":true,"balance_infos":[{"balance":"10.50"},{"balance":"4.50"}]}"#,
        )]);
        let usage = Fetcher::new(&http).fetch(&keyed("deepseek", "sk-ds"));

        assert_eq!(usage.footer, "Available: $15.00");
    }

    #[test]
    fn deepseek_reports_an_unavailable_account() {
        let http = FakeHttp::new(vec![("deepseek.com", 200, r#"{"is_available":false}"#)]);
        let usage = Fetcher::new(&http).fetch(&keyed("deepseek", "sk-ds"));

        assert_eq!(usage.status, crate::model::Status::Degraded);
        assert_eq!(usage.error_message, "Account is unavailable");
    }

    #[test]
    fn gemini_reports_the_most_depleted_bucket() {
        let http = FakeHttp::new(vec![(
            "cloudcode-pa",
            200,
            r#"{"buckets":[{"remainingFraction":0.8},{"remainingFraction":0.25}]}"#,
        )]);
        // Reached through the internal method so the test does not depend on
        // whether this machine has a Gemini login.
        let usage = Fetcher::new(&http).gemini();

        if usage.status == crate::model::Status::Healthy {
            assert_eq!(usage.windows[0].used_percent, 75.0);
        }
    }

    #[test]
    fn copilot_says_why_it_could_not_read_a_quota() {
        let http = FakeHttp::new(vec![("copilot_internal", 404, "not found")]);
        let usage = Fetcher::new(&http).fetch(&keyed("copilot", "gho_test"));

        assert_eq!(usage.status, crate::model::Status::Degraded);
        assert!(usage.error_message.contains("Copilot quota unavailable"));
        assert!(usage.error_message.contains("404"));
    }

    #[test]
    fn copilot_labels_the_account_and_plan() {
        let http = FakeHttp::new(vec![
            (
                "copilot_internal",
                200,
                r#"{"login":"octocat","access_type_sku":"copilot_pro","quota_snapshots":{"chat":{"has_quota":true,"percent_remaining":90.0,"entitlement":100,"remaining":90}}}"#,
            ),
            ("api.github.com/user", 200, r#"{"email":"cat@example.com"}"#),
        ]);
        let usage = Fetcher::new(&http).fetch(&keyed("copilot", "gho_test"));

        assert_eq!(usage.status, crate::model::Status::Healthy);
        // The account label is masked on the way out, like every footer.
        assert_eq!(
            usage.footer,
            "GitHub Copilot (User: octocat, c***t@example.com, Plan: Copilot Pro)"
        );
    }

    #[test]
    fn claude_web_follows_the_organization_then_the_usage_endpoint() {
        // Most specific route first: the usage URL also contains
        // "api/organizations".
        let http = FakeHttp::new(vec![
            (
                "org-123/usage",
                200,
                r#"{"five_hour":{"utilization":30.0},"seven_day":{"utilization":60.0}}"#,
            ),
            ("api/organizations", 200, r#"[{"uuid":"org-123"}]"#),
        ]);
        let mut config = config("claude");
        config.cookie_header = "sessionKey=sk-ant-session; other=1".into();
        let usage = Fetcher::new(&http).fetch(&config);

        assert_eq!(usage.status, crate::model::Status::Healthy);
        assert_eq!(usage.windows.len(), 2);
        assert_eq!(usage.footer, "Claude Web");
        assert_eq!(
            http.header_for("api/organizations", "Cookie").as_deref(),
            Some("sessionKey=sk-ant-session")
        );
    }

    #[test]
    fn claude_web_without_an_organization_says_so() {
        let http = FakeHttp::new(vec![("api/organizations", 200, "[]")]);
        let mut config = config("claude");
        config.cookie_header = "sk-ant-session".into();
        let usage = Fetcher::new(&http).fetch(&config);

        assert_eq!(usage.error_message, "No organization UUID found");
    }

    #[test]
    fn a_provider_without_credentials_is_unconfigured() {
        let http = FakeHttp::new(vec![]);
        for id in [
            "openai",
            "deepseek",
            "openrouter",
            "cursor",
            "groq",
            "bedrock",
        ] {
            let usage = Fetcher::new(&http).fetch(&config(id));
            assert_eq!(
                usage.status,
                crate::model::Status::Unconfigured,
                "{id} should be unconfigured"
            );
        }
        assert_eq!(
            http.request_count(),
            0,
            "unconfigured providers must not call out"
        );
    }

    #[test]
    fn session_keys_are_read_from_either_form() {
        assert_eq!(session_key_from("sk-ant-abc"), "sk-ant-abc");
        assert_eq!(session_key_from("sessionKey=sk-ant-abc"), "sk-ant-abc");
        assert_eq!(
            session_key_from("other=1; sessionKey=sk-ant-abc; more=2"),
            "sk-ant-abc"
        );
    }

    #[test]
    fn footers_omit_the_parts_that_are_unknown() {
        assert_eq!(account_footer("Claude CLI", &["", ""]), "Claude CLI");
        assert_eq!(
            account_footer("Claude CLI", &["a@b.com", ""]),
            "Claude CLI (a@b.com)"
        );
        assert_eq!(
            account_footer("Claude CLI", &["a@b.com", "Max"]),
            "Claude CLI (a@b.com, Max)"
        );
    }

    #[test]
    fn counts_are_pluralised() {
        assert_eq!(plural(1, "model"), "1 model");
        assert_eq!(plural(0, "model"), "0 models");
        assert_eq!(plural(3, "group"), "3 groups");
    }

    #[test]
    fn long_error_bodies_are_truncated() {
        let long = "x".repeat(500);
        let short = truncate(&long, 200);
        assert_eq!(short.chars().count(), 203);
        assert!(short.ends_with("..."));
        assert_eq!(truncate("short", 200), "short");
    }

    #[test]
    fn fetch_all_keeps_results_aligned_with_their_configs() {
        let http = FakeHttp::new(vec![("zen/go/v1/usage", 200, OPENCODE_USAGE)]);
        let configs = vec![
            keyed("opencode", "sk-test"),
            config("cursor"),
            config("groq"),
        ];
        let results = fetch_all(&http, &configs);

        assert_eq!(results.len(), 3);
        assert_eq!(results[0].id, "opencode");
        assert_eq!(results[1].id, "cursor");
        assert_eq!(results[2].id, "groq");
    }
}
