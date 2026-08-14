//! ChatGPT Codex usage, from `chatgpt.com/backend-api/wham/usage`.

use crate::model::UsageWindow;
use crate::time::{DAY, HOUR, countdown_between};
use serde::Deserialize;

#[derive(Debug, Deserialize)]
struct Payload {
    #[serde(default)]
    plan_type: String,
    #[serde(default)]
    rate_limit: RateLimit,
}

#[derive(Debug, Default, Deserialize)]
struct RateLimit {
    primary_window: Option<Window>,
    secondary_window: Option<Window>,
}

#[derive(Debug, Deserialize)]
struct Window {
    #[serde(default)]
    used_percent: f64,
    #[serde(default)]
    limit_window_seconds: i64,
    reset_after_seconds: Option<i64>,
    reset_at: Option<i64>,
}

impl Window {
    fn into_usage(self, fallback_label: &str, now: i64) -> UsageWindow {
        let reset = match (self.reset_at, self.reset_after_seconds) {
            (Some(at), _) if at > 0 => countdown_between(at, now),
            (_, Some(after)) => countdown_between(now + after, now),
            // The window exists but the response said nothing about when it
            // comes back; "Resets now" would be a guess, and a wrong one.
            _ => "Unknown".to_string(),
        };
        UsageWindow::new(
            label_for(fallback_label, self.limit_window_seconds),
            self.used_percent,
        )
        .reset(reset)
        .seconds(self.limit_window_seconds)
    }
}

/// Name a window by how long it runs, since "primary" and "secondary" say
/// nothing to the person reading the bar. Codex moves which window is primary
/// between plans, so the duration is the stable identity.
fn label_for(fallback: &str, seconds: i64) -> String {
    match seconds {
        s if s == 5 * HOUR => "Session".to_string(),
        s if s == 7 * DAY => "Weekly".to_string(),
        _ => fallback.to_string(),
    }
}

#[derive(Debug, Default)]
pub struct CodexUsage {
    pub plan_type: String,
    pub windows: Vec<UsageWindow>,
}

/// Parse the usage response. Fails when it carries no windows at all, which is
/// how an unexpected shape is caught rather than reported as 0% used.
pub fn parse(body: &str, now: i64) -> Result<CodexUsage, String> {
    let payload: Payload =
        serde_json::from_str(body).map_err(|e| format!("unexpected Codex response: {e}"))?;

    let windows: Vec<UsageWindow> = [
        payload
            .rate_limit
            .primary_window
            .map(|w| w.into_usage("Primary", now)),
        payload
            .rate_limit
            .secondary_window
            .map(|w| w.into_usage("Secondary", now)),
    ]
    .into_iter()
    .flatten()
    .collect();

    if windows.is_empty() {
        return Err("no Codex rate-limit windows in response".into());
    }
    Ok(CodexUsage {
        plan_type: payload.plan_type,
        windows,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: i64 = 1_700_000_000;

    #[test]
    fn reads_both_windows_with_durations_as_labels() {
        let usage = parse(
            r#"{
                "plan_type":"plus",
                "rate_limit":{
                    "primary_window":{"used_percent":42,"limit_window_seconds":18000,"reset_at":1700003600},
                    "secondary_window":{"used_percent":5,"limit_window_seconds":604800,"reset_after_seconds":7200}
                }
            }"#,
            NOW,
        )
        .unwrap();

        assert_eq!(usage.plan_type, "plus");
        assert_eq!(usage.windows.len(), 2);

        let session = &usage.windows[0];
        assert_eq!(session.label, "Session");
        assert_eq!(session.used_percent, 42.0);
        assert_eq!(session.window_seconds, 18000);
        assert_eq!(session.reset_countdown, "1h 0m");

        let weekly = &usage.windows[1];
        assert_eq!(weekly.label, "Weekly");
        assert_eq!(weekly.reset_countdown, "2h 0m");
    }

    #[test]
    fn labels_a_weekly_primary_window_by_its_duration() {
        let usage = parse(
            r#"{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800,"reset_after_seconds":590684}}}"#,
            NOW,
        )
        .unwrap();
        assert_eq!(usage.windows[0].label, "Weekly");
    }

    #[test]
    fn keeps_a_positional_label_for_an_unrecognised_duration() {
        let usage = parse(
            r#"{"rate_limit":{"primary_window":{"limit_window_seconds":86400}}}"#,
            NOW,
        )
        .unwrap();
        assert_eq!(usage.windows[0].label, "Primary");
    }

    #[test]
    fn a_window_with_no_reset_information_says_unknown() {
        let usage = parse(
            r#"{"rate_limit":{"primary_window":{"limit_window_seconds":86400}}}"#,
            NOW,
        )
        .unwrap();
        assert_eq!(usage.windows[0].reset_countdown, "Unknown");
    }

    #[test]
    fn a_response_without_windows_is_an_error() {
        assert!(parse(r#"{"rate_limit":{}}"#, NOW).is_err());
        assert!(parse("{}", NOW).is_err());
        assert!(parse("not json", NOW).is_err());
    }

    #[test]
    fn percentages_outside_the_range_are_clamped() {
        let usage = parse(
            r#"{"rate_limit":{"primary_window":{"used_percent":140,"limit_window_seconds":18000}}}"#,
            NOW,
        )
        .unwrap();
        assert_eq!(usage.windows[0].used_percent, 100.0);
    }
}
