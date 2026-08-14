//! Claude usage, from either the `claude.ai` web API or the OAuth endpoint the
//! Claude Code CLI authenticates against. Both return the same shape.

use crate::model::UsageWindow;
use crate::time::{DAY, HOUR, format_countdown};
use serde::Deserialize;

#[derive(Debug, Deserialize)]
struct Payload {
    five_hour: Option<Bucket>,
    seven_day: Option<Bucket>,
}

#[derive(Debug, Deserialize)]
struct Bucket {
    #[serde(default)]
    utilization: Option<f64>,
    #[serde(default)]
    resets_at: Option<String>,
}

impl Bucket {
    fn into_window(self, label: &str, seconds: i64) -> UsageWindow {
        let countdown = match self.resets_at.as_deref().map(str::trim) {
            None | Some("") => "Unknown".to_string(),
            Some(at) => format_countdown(at),
        };
        UsageWindow::new(label, self.utilization.unwrap_or(0.0))
            .reset(countdown)
            .seconds(seconds)
    }
}

/// Parse the usage response into display windows.
///
/// A spent weekly allowance hides the session window. The two are nested: once
/// the week is gone, a session that still shows headroom is not headroom, and
/// showing "Session 12%" next to "Weekly 100%" reads as though there is
/// something left to spend.
pub fn parse(body: &str) -> Vec<UsageWindow> {
    let Ok(payload) = serde_json::from_str::<Payload>(body) else {
        return Vec::new();
    };

    let weekly = payload.seven_day.map(|b| b.into_window("Weekly", 7 * DAY));
    let weekly_spent = weekly.as_ref().is_some_and(UsageWindow::is_spent);
    let session = payload
        .five_hour
        .filter(|_| !weekly_spent)
        .map(|b| b.into_window("Session", 5 * HOUR));

    [session, weekly].into_iter().flatten().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_both_windows_session_first() {
        let windows = parse(
            r#"{
                "five_hour": { "utilization": 45.0, "resets_at": "2026-07-31T20:00:00Z" },
                "seven_day": { "utilization": 80.0, "resets_at": "2026-08-05T00:00:00Z" }
            }"#,
        );

        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].used_percent, 45.0);
        assert_eq!(windows[0].window_seconds, 5 * HOUR);
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].used_percent, 80.0);
    }

    #[test]
    fn a_spent_week_hides_the_session_window() {
        let windows = parse(
            r#"{
                "five_hour": { "utilization": 0.0, "resets_at": "2026-07-31T20:00:00Z" },
                "seven_day": { "utilization": 100.0, "resets_at": "2026-08-05T00:00:00Z" }
            }"#,
        );

        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].label, "Weekly");
    }

    #[test]
    fn either_window_can_stand_alone() {
        let session_only = parse(r#"{"five_hour":{"utilization":12.0}}"#);
        assert_eq!(session_only.len(), 1);
        assert_eq!(session_only[0].label, "Session");

        let weekly_only = parse(r#"{"seven_day":{"utilization":12.0}}"#);
        assert_eq!(weekly_only.len(), 1);
        assert_eq!(weekly_only[0].label, "Weekly");
    }

    #[test]
    fn a_missing_reset_time_is_reported_as_unknown() {
        let windows = parse(r#"{"five_hour":{"utilization":10.0}}"#);
        assert_eq!(windows[0].reset_countdown, "Unknown");
    }

    #[test]
    fn null_fields_in_bucket_survive_deserialization() {
        let windows = parse(r#"{"five_hour":{"utilization":0.0,"resets_at":null},"seven_day":{"utilization":99.0,"resets_at":"2026-08-18T10:59:59.644698+00:00"}}"#);
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].label, "Session");
        assert_eq!(windows[0].used_percent, 0.0);
        assert_eq!(windows[0].reset_countdown, "Unknown");
        assert_eq!(windows[1].label, "Weekly");
        assert_eq!(windows[1].used_percent, 99.0);
    }

    #[test]
    fn an_unusable_payload_yields_no_windows() {
        assert!(parse("{}").is_empty());
        assert!(parse("not json").is_empty());
        assert!(parse("[]").is_empty());
    }
}
