//! OpenCode Go subscription usage, from `opencode.ai/zen/go/v1/usage`.
//!
//! The subscription runs three nested allowances at once — a short rolling
//! window, a weekly cap, and a monthly cap — and any one of them can be the
//! thing that stops the next request. All three are shown, in the order they
//! bite.

use crate::model::UsageWindow;
use crate::time::{DAY, HOUR, WEEK, format_countdown};
use serde::Deserialize;

#[derive(Debug, Deserialize)]
struct Payload {
    #[serde(default)]
    usage: Usage,
}

#[derive(Debug, Default, Deserialize)]
struct Usage {
    rolling: Option<Bucket>,
    weekly: Option<Bucket>,
    monthly: Option<Bucket>,
}

#[derive(Debug, Deserialize)]
struct Bucket {
    /// `ok` while there is headroom, `rate-limited` once the window is spent.
    #[serde(default)]
    status: String,
    #[serde(default)]
    percent: f64,
    #[serde(default, rename = "resetsAt")]
    resets_at: String,
}

impl Bucket {
    fn into_window(self, label: &str, seconds: i64) -> UsageWindow {
        // The service reports whole percents, so a spent window arrives as 100.
        // `status` is the authority regardless: a window it calls rate-limited
        // is spent even if the rounded percentage has not caught up.
        let percent = if self.is_rate_limited() {
            100.0
        } else {
            self.percent
        };
        let countdown = match self.resets_at.trim() {
            "" => "Unknown".to_string(),
            at => format_countdown(at),
        };
        UsageWindow::new(label, percent)
            .reset(countdown)
            .seconds(seconds)
    }

    fn is_rate_limited(&self) -> bool {
        self.status.eq_ignore_ascii_case("rate-limited")
            || self.status.eq_ignore_ascii_case("rate_limited")
    }
}

/// Parse the usage response into display windows, shortest window first.
pub fn parse(body: &str) -> Vec<UsageWindow> {
    let Ok(payload) = serde_json::from_str::<Payload>(body) else {
        return Vec::new();
    };

    [
        payload
            .usage
            .rolling
            .map(|b| b.into_window("Rolling", 5 * HOUR)),
        payload.usage.weekly.map(|b| b.into_window("Weekly", WEEK)),
        payload
            .usage
            .monthly
            .map(|b| b.into_window("Monthly", 30 * DAY)),
    ]
    .into_iter()
    .flatten()
    .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The exact shape the live endpoint returns.
    const LIVE: &str = r#"{"usage":{
        "rolling":{"status":"ok","percent":6,"resetsAt":"2026-08-14T21:01:47.112Z"},
        "weekly":{"status":"rate-limited","percent":100,"resetsAt":"2026-08-17T00:00:00.112Z"},
        "monthly":{"status":"ok","percent":50,"resetsAt":"2026-09-12T22:42:28.112Z"}
    }}"#;

    #[test]
    fn reads_all_three_windows_shortest_first() {
        let windows = parse(LIVE);

        assert_eq!(windows.len(), 3);
        assert_eq!(windows[0].label, "Rolling");
        assert_eq!(windows[0].used_percent, 6.0);
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].used_percent, 100.0);
        assert_eq!(windows[2].label, "Monthly");
        assert_eq!(windows[2].used_percent, 50.0);
    }

    #[test]
    fn window_durations_are_recorded() {
        let windows = parse(LIVE);
        assert_eq!(windows[1].window_seconds, WEEK);
        assert_eq!(windows[2].window_seconds, 30 * DAY);
    }

    #[test]
    fn fractional_reset_timestamps_produce_a_countdown() {
        let windows = parse(LIVE);
        for window in &windows {
            assert_ne!(
                window.reset_countdown, "Unknown",
                "{} lost its reset time",
                window.label
            );
        }
    }

    #[test]
    fn a_rate_limited_status_wins_over_a_lagging_percentage() {
        let windows = parse(
            r#"{"usage":{"weekly":{"status":"rate-limited","percent":99,"resetsAt":"2026-08-17T00:00:00Z"}}}"#,
        );
        assert_eq!(windows[0].used_percent, 100.0);
        assert!(windows[0].is_spent());
    }

    #[test]
    fn a_partial_payload_reports_only_what_it_carries() {
        let windows = parse(r#"{"usage":{"monthly":{"status":"ok","percent":3}}}"#);
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Monthly");
        assert_eq!(windows[0].reset_countdown, "Unknown");
    }

    #[test]
    fn an_unusable_payload_yields_no_windows() {
        assert!(parse("{}").is_empty());
        assert!(parse(r#"{"usage":{}}"#).is_empty());
        assert!(parse("not json").is_empty());
    }
}
