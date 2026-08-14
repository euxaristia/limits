//! GitHub Copilot quotas, from the endpoint the editor plugins read.
//!
//! The documented REST API only reports Copilot *seats*, which is an
//! organisation-level number. Per-user quota lives on
//! `api.github.com/copilot_internal/user`, which is undocumented and can change
//! without notice — hence a parser that reports what it can and says so
//! plainly when it cannot, rather than inventing a healthy zero.

use crate::model::UsageWindow;
use crate::time::{DAY, format_countdown};
use serde::Deserialize;
use std::collections::BTreeMap;

/// Quotas GitHub reports, in the order they are shown. Anything GitHub adds
/// later is appended after these, alphabetically.
const KNOWN_QUOTAS: [(&str, &str); 3] = [
    ("premium_interactions", "Premium"),
    ("chat", "Chat"),
    ("completions", "Completions"),
];

const PLAN_NAMES: [(&str, &str); 4] = [
    ("free_limited_copilot", "Copilot Free"),
    ("copilot_for_business", "Copilot Business"),
    ("copilot_enterprise", "Copilot Enterprise"),
    ("copilot_pro", "Copilot Pro"),
];

#[derive(Debug, Default, Deserialize)]
pub struct CopilotUser {
    #[serde(default)]
    pub login: String,
    #[serde(default)]
    pub access_type_sku: String,
    #[serde(default)]
    pub copilot_plan: String,
    #[serde(default, rename = "quota_reset_date_utc")]
    pub quota_reset: String,
    /// Sorted by key, so an unrecognised quota lands in a stable position
    /// instead of moving on every read.
    #[serde(default, rename = "quota_snapshots")]
    pub quotas: BTreeMap<String, Quota>,
}

#[derive(Debug, Default, Deserialize)]
pub struct Quota {
    #[serde(default)]
    pub unlimited: bool,
    #[serde(default)]
    pub has_quota: bool,
    #[serde(default)]
    pub percent_remaining: f64,
    #[serde(default)]
    pub entitlement: f64,
    /// GitHub sends what is left, not what is gone.
    #[serde(default)]
    pub remaining: Option<f64>,
}

impl Quota {
    /// How much of the quota is spent, as a percentage and as a count.
    fn spent(&self) -> (f64, f64) {
        let percent = (100.0 - self.percent_remaining).clamp(0.0, 100.0);
        let used = match self.remaining {
            Some(remaining) => self.entitlement - remaining,
            None => self.entitlement * percent / 100.0,
        };
        (percent, used.max(0.0))
    }
}

impl CopilotUser {
    pub fn plan_label(&self) -> &str {
        PLAN_NAMES
            .iter()
            .find(|(sku, _)| *sku == self.access_type_sku)
            .map(|(_, name)| *name)
            .unwrap_or(&self.copilot_plan)
    }
}

pub fn parse(body: &str) -> Result<CopilotUser, String> {
    serde_json::from_str(body).map_err(|_| "unexpected response shape".to_string())
}

/// Turn the reported quotas into display rows, and report whether any of them
/// is spent.
pub fn windows(payload: &CopilotUser) -> (Vec<UsageWindow>, bool) {
    let countdown = reset_countdown(&payload.quota_reset);

    let known: Vec<&str> = KNOWN_QUOTAS
        .iter()
        .map(|(id, _)| *id)
        .filter(|id| payload.quotas.contains_key(*id))
        .collect();
    let extras = payload
        .quotas
        .keys()
        .map(String::as_str)
        .filter(|id| !known.contains(id));

    let mut windows = Vec::new();
    let mut exhausted = false;

    for id in known.iter().copied().chain(extras) {
        let Some(quota) = payload.quotas.get(id) else {
            continue;
        };
        // GitHub lists every quota it knows about, including ones this plan has
        // no entitlement to. Showing those at 0% would imply they are usable.
        if !quota.has_quota && !quota.unlimited {
            continue;
        }

        let label = label_for(id);
        if quota.unlimited {
            windows.push(
                UsageWindow::new(label, 0.0)
                    .reset(countdown.clone())
                    .seconds(30 * DAY)
                    .text("Unlimited"),
            );
            continue;
        }

        let (percent, used) = quota.spent();
        exhausted |= percent >= 100.0;
        windows.push(
            UsageWindow::new(label, percent)
                .reset(countdown.clone())
                .seconds(30 * DAY)
                .text(format!(
                    "{used:.0} / {:.0} ({percent:.1}% used)",
                    quota.entitlement
                )),
        );
    }

    (windows, exhausted)
}

/// GitHub states the reset as a bare `YYYY-MM-DD` date, which is midnight UTC.
fn reset_countdown(raw: &str) -> String {
    let raw = raw.trim();
    if raw.is_empty() {
        return String::new();
    }
    if raw.len() == 10 && raw.as_bytes()[4] == b'-' {
        return format_countdown(&format!("{raw}T00:00:00Z"));
    }
    format_countdown(raw)
}

fn label_for(id: &str) -> String {
    if let Some((_, label)) = KNOWN_QUOTAS.iter().find(|(known, _)| *known == id) {
        return (*label).to_string();
    }
    let mut chars = id.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().collect::<String>() + &chars.as_str().replace('_', " "),
        None => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn labels(windows: &[UsageWindow]) -> Vec<&str> {
        windows.iter().map(|w| w.label.as_str()).collect()
    }

    fn text_of<'a>(windows: &'a [UsageWindow], label: &str) -> &'a str {
        windows
            .iter()
            .find(|w| w.label == label)
            .unwrap_or_else(|| panic!("no window labelled {label} in {:?}", labels(windows)))
            .percent_text_override
            .as_deref()
            .unwrap_or_else(|| panic!("window {label} has no percent text"))
    }

    /// The shape GitHub returns for a Copilot Free account: premium
    /// interactions are listed but not entitled.
    const FREE_ACCOUNT: &str = r#"{
        "login": "octocat",
        "access_type_sku": "free_limited_copilot",
        "copilot_plan": "individual",
        "quota_snapshots": {
            "chat": {"has_quota": true, "percent_remaining": 98.0, "entitlement": 200, "remaining": 196},
            "completions": {"has_quota": true, "percent_remaining": 100.0, "entitlement": 2000, "remaining": 2000},
            "premium_interactions": {"has_quota": false, "percent_remaining": 0.0, "entitlement": 0, "remaining": 0}
        }
    }"#;

    #[test]
    fn skips_quotas_this_plan_is_not_entitled_to() {
        let (windows, exhausted) = windows(&parse(FREE_ACCOUNT).unwrap());

        assert_eq!(labels(&windows), ["Chat", "Completions"]);
        assert!(!exhausted, "account with quota left reported as exhausted");
    }

    #[test]
    fn reports_what_is_used_not_what_is_left() {
        let (windows, _) = windows(&parse(FREE_ACCOUNT).unwrap());

        assert_eq!(text_of(&windows, "Chat"), "4 / 200 (2.0% used)");
        assert_eq!(
            windows
                .iter()
                .find(|w| w.label == "Chat")
                .unwrap()
                .used_percent,
            2.0
        );
    }

    #[test]
    fn falls_back_to_the_percentage_when_no_count_is_sent() {
        let (windows, _) = windows(
            &parse(r#"{"quota_snapshots":{"chat":{"has_quota":true,"percent_remaining":75.0,"entitlement":200}}}"#)
                .unwrap(),
        );
        assert_eq!(text_of(&windows, "Chat"), "50 / 200 (25.0% used)");
    }

    #[test]
    fn flags_a_spent_quota() {
        let (windows, exhausted) = windows(
            &parse(r#"{"quota_snapshots":{"chat":{"has_quota":true,"percent_remaining":0.0,"entitlement":200,"remaining":0}}}"#)
                .unwrap(),
        );

        assert!(exhausted, "spent quota not flagged as exhausted");
        assert_eq!(text_of(&windows, "Chat"), "200 / 200 (100.0% used)");
    }

    #[test]
    fn an_unlimited_quota_shows_no_bar() {
        let (windows, exhausted) = windows(
            &parse(r#"{"quota_snapshots":{"completions":{"unlimited":true,"has_quota":false}}}"#)
                .unwrap(),
        );

        assert_eq!(text_of(&windows, "Completions"), "Unlimited");
        assert_eq!(windows[0].used_percent, 0.0);
        assert!(!exhausted, "unlimited quota reported as exhausted");
    }

    #[test]
    fn a_quota_github_adds_later_still_appears_after_the_known_ones() {
        let (windows, _) = windows(
            &parse(r#"{"quota_snapshots":{
                "chat":{"has_quota":true,"percent_remaining":100.0,"entitlement":200,"remaining":200},
                "agent_mode":{"has_quota":true,"percent_remaining":50.0,"entitlement":10,"remaining":5}
            }}"#)
            .unwrap(),
        );

        assert_eq!(labels(&windows), ["Chat", "Agent mode"]);
    }

    #[test]
    fn an_empty_payload_reports_nothing() {
        let (windows, exhausted) = windows(&parse("{}").unwrap());
        assert!(windows.is_empty());
        assert!(!exhausted);
    }

    #[test]
    fn a_bare_reset_date_becomes_a_countdown() {
        assert_eq!(reset_countdown(""), "");
        assert_eq!(reset_countdown("1999-01-01"), "Resets now");
        assert_ne!(reset_countdown("2999-01-01"), "Unknown");
    }

    #[test]
    fn plan_labels_fall_back_to_the_raw_plan_name() {
        let free: CopilotUser = parse(FREE_ACCOUNT).unwrap();
        assert_eq!(free.plan_label(), "Copilot Free");

        let unknown: CopilotUser =
            parse(r#"{"access_type_sku":"something_new","copilot_plan":"business"}"#).unwrap();
        assert_eq!(unknown.plan_label(), "business");
    }
}
