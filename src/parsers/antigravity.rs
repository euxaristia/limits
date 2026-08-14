//! Antigravity quota, from Google's `cloudcode-pa` internal endpoints.
//!
//! Two response shapes are in the wild. Newer builds return `groups`, where the
//! service has already bundled models into families and states a window per
//! family. Older ones return a flat `buckets` array, one entry per model, which
//! has to be regrouped here. Both end up as the same list of buckets.

use crate::time::{countdown_seconds, format_countdown, now_unix, parse_rfc3339};
use serde_json::Value;
use std::collections::BTreeMap;

/// Which allowance a bucket belongs to.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Timeframe {
    /// Session is ranked first deliberately: if the session window is spent,
    /// the weekly one is moot until it resets, so it is the more urgent number.
    Session,
    Weekly,
    /// The service gave no reset time, so the window has no known length.
    Quota,
}

impl Timeframe {
    pub fn label(self) -> &'static str {
        match self {
            Timeframe::Session => "Session",
            Timeframe::Weekly => "Weekly",
            Timeframe::Quota => "Quota",
        }
    }
}

#[derive(Clone, Debug)]
pub struct Bucket {
    pub group_label: String,
    pub members: Vec<String>,
    pub used_percent: f64,
    pub remaining_percent: f64,
    pub reset_countdown: String,
    pub timeframe: Timeframe,
}

/// Model families, ranked for display. Gemini first because it is the family
/// Antigravity meters most tightly.
fn family_rank(label: &str) -> u8 {
    if label.eq_ignore_ascii_case("Gemini") {
        0
    } else if label.to_ascii_lowercase().starts_with("claude") {
        1
    } else {
        2
    }
}

fn family_from_model_id(model_id: &str) -> &'static str {
    let m = model_id.to_ascii_lowercase();
    if m.contains("gemini") {
        "Gemini"
    } else if m.contains("claude") || m.starts_with("gpt-") || m.starts_with("gpt_") {
        "Claude & GPT"
    } else if m.starts_with("chat_") || m.starts_with("tab_") {
        "Internal"
    } else {
        "Antigravity"
    }
}

/// Collapse a versioned model id to the name a person recognises, so a family
/// listing reads "Gemini Pro, Gemini Flash" rather than four dated variants.
pub fn canonical_model_name(model_id: &str) -> String {
    let m = model_id.to_ascii_lowercase();
    let name = if m.contains("flash-lite") || m.contains("flash_lite") || m.contains("flashlite") {
        "Gemini Flash-Lite"
    } else if m.contains("flash") {
        "Gemini Flash"
    } else if m.contains("pro") || m.contains("gemini") {
        "Gemini Pro"
    } else if m.contains("sonnet") {
        "Claude Sonnet"
    } else if m.contains("opus") {
        "Claude Opus"
    } else if m.contains("gpt") {
        "GPT"
    } else {
        return model_id.to_string();
    };
    name.to_string()
}

/// Classify a window by how far away its reset is. A reset inside eight hours
/// is a session; anything further out is the weekly allowance.
fn derive_timeframe(reset_iso: &str, countdown: &str) -> Timeframe {
    if reset_iso.trim().is_empty() {
        return Timeframe::Quota;
    }
    if let Some(target) = parse_rfc3339(reset_iso) {
        return if target - now_unix() <= 8 * crate::time::HOUR {
            Timeframe::Session
        } else {
            Timeframe::Weekly
        };
    }
    // Unparseable timestamp: fall back to reading the rendered countdown.
    match countdown_seconds(countdown) {
        Some(seconds) if seconds <= 8 * crate::time::HOUR => Timeframe::Session,
        Some(_) => Timeframe::Weekly,
        None => Timeframe::Quota,
    }
}

fn countdown_for(reset_iso: &str) -> String {
    if reset_iso.trim().is_empty() {
        "Never Resets".to_string()
    } else {
        format_countdown(reset_iso)
    }
}

fn percentages(remaining_fraction: f64) -> (f64, f64) {
    (
        ((1.0 - remaining_fraction) * 100.0).clamp(0.0, 100.0),
        (remaining_fraction * 100.0).clamp(0.0, 100.0),
    )
}

/// Parse either response shape.
pub fn parse(body: &str) -> Vec<Bucket> {
    let Ok(root) = serde_json::from_str::<Value>(body) else {
        return Vec::new();
    };

    let mut buckets = match root.get("groups").and_then(Value::as_array) {
        Some(groups) if !groups.is_empty() => from_groups(groups),
        _ => match root.get("buckets").and_then(Value::as_array) {
            Some(raw) => from_flat_buckets(raw),
            None => return Vec::new(),
        },
    };

    buckets.sort_by(|a, b| {
        family_rank(&a.group_label)
            .cmp(&family_rank(&b.group_label))
            .then(a.timeframe.cmp(&b.timeframe))
            .then_with(|| sort_key(&a.reset_countdown).cmp(&sort_key(&b.reset_countdown)))
    });
    hide_sessions_under_a_spent_week(buckets)
}

/// Rank by time to reset, with an unreadable countdown last rather than
/// wherever its text happens to sort.
fn sort_key(countdown: &str) -> (bool, i64) {
    match countdown_seconds(countdown) {
        Some(seconds) => (false, seconds),
        None => (true, 0),
    }
}

fn from_groups(groups: &[Value]) -> Vec<Bucket> {
    let mut result = Vec::new();

    for group in groups {
        let name = group
            .get("displayName")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let lower = name.to_ascii_lowercase();
        let family = if lower.starts_with("gemini") {
            "Gemini"
        } else if lower.contains("claude") || lower.contains("gpt") {
            "Claude & GPT"
        } else {
            "Antigravity"
        };

        let description = group
            .get("description")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let members = group_members(family, description);

        let Some(entries) = group.get("buckets").and_then(Value::as_array) else {
            continue;
        };
        for entry in entries {
            if entry
                .get("disabled")
                .and_then(Value::as_bool)
                .unwrap_or(false)
            {
                continue;
            }
            let remaining = entry
                .get("remainingFraction")
                .and_then(Value::as_f64)
                .unwrap_or(0.0);
            let reset_iso = entry
                .get("resetTime")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let countdown = countdown_for(reset_iso);

            // The service names the window outright here; the timestamp is
            // only consulted when it does not.
            let timeframe = match entry.get("window").and_then(Value::as_str) {
                Some(w) if w.eq_ignore_ascii_case("weekly") => Timeframe::Weekly,
                Some(w) if w.eq_ignore_ascii_case("5h") => Timeframe::Session,
                _ => derive_timeframe(reset_iso, &countdown),
            };

            let (used, remaining_percent) = percentages(remaining);
            result.push(Bucket {
                group_label: family.to_string(),
                members: members.clone(),
                used_percent: used,
                remaining_percent,
                reset_countdown: countdown,
                timeframe,
            });
        }
    }
    result
}

/// The models a group covers.
///
/// The service's `description` is marketing copy rather than a model list, so
/// the two families it actually meters are named explicitly. Anything else
/// falls back to whatever follows a colon in the description.
fn group_members(family: &str, description: &str) -> Vec<String> {
    match family {
        "Gemini" => ["Gemini 3.6 Flash", "Gemini 3.5 Flash", "Gemini 3.1 Pro"]
            .iter()
            .map(|s| s.to_string())
            .collect(),
        "Claude & GPT" => ["Claude Sonnet 4.6", "Claude Opus 4.6", "GPT-OSS 120B"]
            .iter()
            .map(|s| s.to_string())
            .collect(),
        _ => {
            let list = match description.split_once(':') {
                Some((_, rest)) => rest,
                None => description,
            };
            list.split(',')
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(str::to_string)
                .collect()
        }
    }
}

fn from_flat_buckets(raw: &[Value]) -> Vec<Bucket> {
    // Grouped by family, window, and reset time: two models sharing all three
    // are sharing one allowance, so they belong on one row.
    let mut groups: BTreeMap<(String, Timeframe, String), Vec<(String, f64)>> = BTreeMap::new();

    for entry in raw {
        let Some(model_id) = entry.get("modelId").and_then(Value::as_str) else {
            continue;
        };
        let lower = model_id.to_ascii_lowercase();
        // Editor-internal buckets, not something the user spends.
        if lower.starts_with("chat_") || lower.starts_with("tab_") {
            continue;
        }

        let remaining = entry
            .get("remainingFraction")
            .and_then(Value::as_f64)
            .unwrap_or(0.0);
        let reset_iso = entry
            .get("resetTime")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let countdown = countdown_for(reset_iso);
        let timeframe = derive_timeframe(reset_iso, &countdown);

        groups
            .entry((
                family_from_model_id(model_id).to_string(),
                timeframe,
                countdown,
            ))
            .or_default()
            .push((canonical_model_name(model_id), remaining));
    }

    groups
        .into_iter()
        .map(|((family, timeframe, countdown), entries)| {
            // The group is only as usable as its most depleted model.
            let lowest = entries
                .iter()
                .map(|(_, remaining)| *remaining)
                .fold(f64::INFINITY, f64::min);
            let mut members: Vec<String> = entries.into_iter().map(|(name, _)| name).collect();
            members.sort();
            members.dedup();

            let (used, remaining_percent) = percentages(lowest);
            Bucket {
                group_label: family,
                members,
                used_percent: used,
                remaining_percent,
                reset_countdown: countdown,
                timeframe,
            }
        })
        .collect()
}

/// Drop session rows for families whose weekly allowance is already spent.
/// Session headroom under a spent week is not headroom.
fn hide_sessions_under_a_spent_week(buckets: Vec<Bucket>) -> Vec<Bucket> {
    let spent: Vec<&str> = buckets
        .iter()
        .filter(|b| b.timeframe == Timeframe::Weekly && b.used_percent >= 100.0)
        .map(|b| b.group_label.as_str())
        .collect();
    if spent.is_empty() {
        return buckets;
    }
    let spent: Vec<String> = spent.into_iter().map(str::to_string).collect();

    buckets
        .into_iter()
        .filter(|b| b.timeframe != Timeframe::Session || !spent.contains(&b.group_label))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::time::{DAY, HOUR};

    fn in_hours(hours: i64) -> String {
        rfc3339(now_unix() + hours * HOUR)
    }

    /// Render a Unix time back to RFC 3339 for test fixtures.
    fn rfc3339(unix: i64) -> String {
        let days = unix.div_euclid(DAY);
        let seconds = unix.rem_euclid(DAY);
        let (year, month, day) = civil_from_days(days);
        format!(
            "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
            seconds / HOUR,
            (seconds % HOUR) / 60,
            seconds % 60
        )
    }

    fn civil_from_days(days: i64) -> (i64, i64, i64) {
        let z = days + 719_468;
        let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
        let doe = z - era * 146_097;
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
        let y = yoe + era * 400;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let d = doy - (153 * mp + 2) / 5 + 1;
        let m = if mp < 10 { mp + 3 } else { mp - 9 };
        (if m <= 2 { y + 1 } else { y }, m, d)
    }

    #[test]
    fn an_empty_payload_yields_nothing() {
        assert!(parse("{}").is_empty());
        assert!(parse("not json").is_empty());
    }

    #[test]
    fn a_single_gemini_bucket_is_named_and_scaled() {
        let json = format!(
            r#"{{"buckets":[{{"modelId":"gemini-3-1-pro-low","remainingFraction":0.45,"resetTime":"{}","tokenType":"WTUS"}}]}}"#,
            in_hours(4)
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 1);
        let bucket = &buckets[0];
        assert_eq!(bucket.group_label, "Gemini");
        assert_eq!(bucket.members, ["Gemini Pro"]);
        assert!((bucket.used_percent - 55.0).abs() < 1.0);
        assert!((bucket.remaining_percent - 45.0).abs() < 1.0);
        assert!(
            bucket.reset_countdown.starts_with("3h") || bucket.reset_countdown.starts_with("4h"),
            "unexpected countdown {}",
            bucket.reset_countdown
        );
    }

    #[test]
    fn models_sharing_a_window_share_a_row_at_the_lowest_headroom() {
        let reset = in_hours(4);
        let json = format!(
            r#"{{"buckets":[
                {{"modelId":"gemini-2-5-pro","remainingFraction":0.50,"resetTime":"{reset}"}},
                {{"modelId":"gemini-3-1-flash-lite","remainingFraction":0.20,"resetTime":"{reset}"}}
            ]}}"#
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 1);
        assert_eq!(buckets[0].group_label, "Gemini");
        assert_eq!(buckets[0].members.len(), 2);
        assert_eq!(buckets[0].remaining_percent, 20.0);
    }

    #[test]
    fn editor_internal_buckets_are_not_shown() {
        let json = format!(
            r#"{{"buckets":[
                {{"modelId":"gemini-3-1-pro-low","remainingFraction":0.50,"resetTime":"{}"}},
                {{"modelId":"chat_23310","remainingFraction":1.0}},
                {{"modelId":"tab_flash_lite_preview","remainingFraction":1.0}}
            ]}}"#,
            in_hours(4)
        );
        assert_eq!(parse(&json).len(), 1);
    }

    #[test]
    fn gemini_is_ordered_before_claude() {
        let reset = in_hours(4);
        let json = format!(
            r#"{{"buckets":[
                {{"modelId":"claude-opus-4-6","remainingFraction":0.5,"resetTime":"{reset}"}},
                {{"modelId":"gemini-3-1-pro","remainingFraction":0.8,"resetTime":"{reset}"}}
            ]}}"#
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 2);
        assert_eq!(buckets[0].group_label, "Gemini");
        assert_eq!(buckets[1].group_label, "Claude & GPT");
    }

    #[test]
    fn session_is_ordered_before_weekly_even_when_weekly_resets_sooner() {
        let json = format!(
            r#"{{"groups":[{{
                "displayName":"Gemini",
                "description":"Gemini models",
                "buckets":[
                    {{"remainingFraction":0.76,"resetTime":"{}","window":"weekly"}},
                    {{"remainingFraction":0.0,"resetTime":"{}","window":"5h"}}
                ]
            }}]}}"#,
            in_hours(1),
            in_hours(5 * 24)
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 2);
        assert_eq!(buckets[0].timeframe, Timeframe::Session);
        assert_eq!(buckets[1].timeframe, Timeframe::Weekly);
    }

    #[test]
    fn the_flat_shape_orders_by_window_too_not_by_countdown_text() {
        // "10h" sorts before "1h" as text; only a real duration comparison
        // puts the session first.
        let json = format!(
            r#"{{"buckets":[
                {{"modelId":"gemini-3-1-pro","remainingFraction":0.5,"resetTime":"{}"}},
                {{"modelId":"gemini-3-1-flash","remainingFraction":0.2,"resetTime":"{}"}}
            ]}}"#,
            in_hours(10),
            in_hours(1)
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 2);
        assert_eq!(buckets[0].timeframe, Timeframe::Session);
        assert_eq!(buckets[1].timeframe, Timeframe::Weekly);
    }

    #[test]
    fn a_spent_week_hides_that_family_s_session_row() {
        let json = format!(
            r#"{{"groups":[{{
                "displayName":"Claude & GPT",
                "description":"Claude and GPT models",
                "buckets":[
                    {{"remainingFraction":0.0,"resetTime":"{}","window":"weekly"}},
                    {{"remainingFraction":0.5,"resetTime":"{}","window":"5h"}}
                ]
            }}]}}"#,
            in_hours(1),
            in_hours(5)
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 1);
        assert_eq!(buckets[0].timeframe, Timeframe::Weekly);
    }

    #[test]
    fn a_spent_week_leaves_another_family_s_session_alone() {
        let json = format!(
            r#"{{"groups":[
                {{"displayName":"Claude & GPT","description":"c","buckets":[
                    {{"remainingFraction":0.0,"resetTime":"{}","window":"weekly"}}
                ]}},
                {{"displayName":"Gemini","description":"g","buckets":[
                    {{"remainingFraction":0.9,"resetTime":"{}","window":"5h"}}
                ]}}
            ]}}"#,
            in_hours(1),
            in_hours(3)
        );
        let buckets = parse(&json);

        assert_eq!(buckets.len(), 2);
        assert!(buckets.iter().any(|b| b.timeframe == Timeframe::Session));
    }

    #[test]
    fn disabled_buckets_are_skipped() {
        let json = format!(
            r#"{{"groups":[{{"displayName":"Gemini","description":"g","buckets":[
                {{"remainingFraction":0.5,"resetTime":"{}","window":"5h","disabled":true}}
            ]}}]}}"#,
            in_hours(2)
        );
        assert!(parse(&json).is_empty());
    }

    #[test]
    fn model_names_collapse_to_something_recognisable() {
        assert_eq!(
            canonical_model_name("gemini-3-1-flash-lite"),
            "Gemini Flash-Lite"
        );
        assert_eq!(canonical_model_name("gemini-3-6-flash"), "Gemini Flash");
        assert_eq!(canonical_model_name("gemini-3-1-pro-low"), "Gemini Pro");
        assert_eq!(canonical_model_name("claude-sonnet-4-6"), "Claude Sonnet");
        assert_eq!(canonical_model_name("claude-opus-4-6"), "Claude Opus");
        assert_eq!(canonical_model_name("gpt-oss-120b"), "GPT");
        assert_eq!(canonical_model_name("mystery-model"), "mystery-model");
    }

    #[test]
    fn a_bucket_with_no_reset_time_never_resets() {
        let buckets = parse(r#"{"buckets":[{"modelId":"gemini-pro","remainingFraction":0.3}]}"#);
        assert_eq!(buckets[0].reset_countdown, "Never Resets");
        assert_eq!(buckets[0].timeframe, Timeframe::Quota);
    }
}
