//! Meta Muse subscription and rate limit parsing.
//!
//! Meta AI's Responses API (`/v1/responses`) returns usage snapshots during streaming
//! responses (`response.subscription_usage`) and reports rate limit exhaustion via HTTP 429
//! (`rate_limit_exceeded`) with an exact `resets_at` timestamp.
//!
//! Account metadata (tier, email, subscription state) is queried from `/muse-code/key`.

use crate::model::UsageWindow;
use crate::time::{countdown_between, parse_rfc3339};
use serde_json::Value;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct MetaKeyInfo {
    pub email: String,
    pub name: String,
    pub tier: String,
    pub active: bool,
    pub api_key: Option<String>,
}

/// Parse account metadata from the `/muse-code/key` JSON endpoint.
pub fn parse_key_metadata(raw: &str) -> Option<MetaKeyInfo> {
    let value: Value = serde_json::from_str(raw).ok()?;
    let obj = value.as_object()?;

    let email = obj
        .get("user_email")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string();

    let name = obj
        .get("user_full_name")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string();

    let tier = obj
        .get("subs_tier_name")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string();

    let active = obj
        .get("is_subs_active")
        .and_then(Value::as_bool)
        .unwrap_or(false);

    let api_key = obj
        .get("api_key")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|k| !k.is_empty())
        .map(str::to_string);

    Some(MetaKeyInfo {
        email,
        name,
        tier,
        active,
        api_key,
    })
}

/// Parse rate-limit error payload (HTTP 429) from `/v1/responses`.
///
/// Shape:
/// ```json
/// {
///   "error": {
///     "code": "rate_limit_exceeded",
///     "message": "Subscription quota exhausted. Your usage window resets at 2026-10-19T00:00:00Z.",
///     "resets_at": 1792368000,
///     "type": "rate_limit_error"
///   }
/// }
/// ```
pub fn parse_rate_limit(raw: &str, now: i64) -> Option<UsageWindow> {
    let value: Value = serde_json::from_str(raw).ok()?;
    let error = value.get("error")?.as_object()?;

    let code = error
        .get("code")
        .and_then(Value::as_str)
        .unwrap_or_default();
    let err_type = error
        .get("type")
        .and_then(Value::as_str)
        .unwrap_or_default();
    if code != "rate_limit_exceeded" && err_type != "rate_limit_error" {
        return None;
    }

    let message = error
        .get("message")
        .and_then(Value::as_str)
        .unwrap_or_default();

    let resets_at = error
        .get("resets_at")
        .and_then(Value::as_i64)
        .filter(|&t| t > 0)
        .or_else(|| {
            // Fallback: extract RFC 3339 timestamp from message:
            // "Subscription quota exhausted. Your usage window resets at 2026-10-19T00:00:00Z."
            message
                .split("resets at ")
                .nth(1)
                .map(|s| s.trim_end_matches('.'))
                .and_then(parse_rfc3339)
        });

    let countdown = match resets_at {
        Some(target) => countdown_between(target, now),
        None => "Unknown".to_string(),
    };

    Some(
        UsageWindow::new("Quota", 100.0)
            .reset(countdown)
            .text("0.0% remaining"),
    )
}

/// Parse subscription usage from SSE line(s) or JSON body.
///
/// Expected shape inside the event:
/// ```json
/// {
///   "subscription": {
///     "window": {
///       "used_percent": 42.5,
///       "window_duration_mins": 300,
///       "resets_at": 1792368000
///     },
///     "weekly": {
///       "used_percent": 15.0,
///       "resets_at": 1792972800
///     }
///   }
/// }
/// ```
pub fn parse_subscription_usage(raw: &str, now: i64) -> Vec<UsageWindow> {
    // Look for JSON object or SSE data line
    let json_str = if let Some(idx) = raw.find("data:") {
        let after = &raw[idx + 5..];
        after.lines().next().unwrap_or(after).trim()
    } else {
        raw.trim()
    };

    let Ok(value) = serde_json::from_str::<Value>(json_str) else {
        return Vec::new();
    };

    let sub = value
        .get("subscription")
        .or_else(|| value.get("usage"))
        .unwrap_or(&value);

    let mut windows = Vec::new();

    if let Some(w) = sub.get("window").and_then(Value::as_object)
        && let Some(used) = w.get("used_percent").and_then(Value::as_f64)
    {
        let duration_mins = w
            .get("window_duration_mins")
            .and_then(Value::as_i64)
            .unwrap_or(300);
        let label = if duration_mins == 300 {
            "Session (5h)".to_string()
        } else if duration_mins > 0 {
            format!("Session ({}m)", duration_mins)
        } else {
            "Session Window".to_string()
        };
        let mut window = UsageWindow::new(label, used).seconds(duration_mins * 60);
        if let Some(resets_at) = w.get("resets_at").and_then(Value::as_i64) {
            window = window.reset(countdown_between(resets_at, now));
        }
        windows.push(window);
    }

    if let Some(w) = sub.get("weekly").and_then(Value::as_object)
        && let Some(used) = w.get("used_percent").and_then(Value::as_f64)
    {
        let mut window = UsageWindow::new("Weekly", used).seconds(7 * 24 * 3600);
        if let Some(resets_at) = w.get("resets_at").and_then(Value::as_i64) {
            window = window.reset(countdown_between(resets_at, now));
        }
        windows.push(window);
    }

    windows
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY_PAYLOAD: &str = r#"{
        "api_key": "LLM|test-minted-key",
        "base_url": "https://api.meta.ai/v1",
        "has_payment_method": false,
        "require_payment": false,
        "is_subs_active": true,
        "user_full_name": "Mateo Pineda",
        "user_email": "mateo@example.com",
        "subs_tier_name": "Muse Code Power Usage"
    }"#;

    const RATE_LIMIT_PAYLOAD: &str = r#"{
        "error": {
            "code": "rate_limit_exceeded",
            "message": "Subscription quota exhausted. Your usage window resets at 2026-10-19T00:00:00Z.",
            "param": null,
            "resets_at": 1792368000,
            "type": "rate_limit_error"
        }
    }"#;

    const USAGE_SSE_PAYLOAD: &str = "data: {\"type\":\"response.subscription_usage\",\"subscription\":{\"window\":{\"used_percent\":42.5,\"window_duration_mins\":300,\"resets_at\":1792368000},\"weekly\":{\"used_percent\":15.0,\"resets_at\":1792972800}}}\n\n";

    #[test]
    fn parses_key_metadata() {
        let info = parse_key_metadata(KEY_PAYLOAD).expect("expected info");
        assert_eq!(info.email, "mateo@example.com");
        assert_eq!(info.name, "Mateo Pineda");
        assert_eq!(info.tier, "Muse Code Power Usage");
        assert!(info.active);
        assert_eq!(info.api_key.as_deref(), Some("LLM|test-minted-key"));
    }

    #[test]
    fn parses_rate_limit_exhausted_window() {
        let now = 1792368000 - 3600; // 1 hour before reset
        let window = parse_rate_limit(RATE_LIMIT_PAYLOAD, now).expect("expected window");
        assert_eq!(window.label, "Quota");
        assert_eq!(window.used_percent, 100.0);
        assert_eq!(window.reset_countdown, "1h 0m");
        assert_eq!(
            window.percent_text_override.as_deref(),
            Some("0.0% remaining")
        );
    }

    #[test]
    fn parses_subscription_usage_windows() {
        let now = 1792368000 - 7200; // 2 hours before reset
        let windows = parse_subscription_usage(USAGE_SSE_PAYLOAD, now);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session (5h)");
        assert_eq!(windows[0].used_percent, 42.5);
        assert_eq!(windows[0].reset_countdown, "2h 0m");
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].used_percent, 15.0);
    }
}
