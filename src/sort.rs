//! Deciding what to show first.
//!
//! A quota report is read at a glance, usually to answer one question: what can
//! I use right now. So the ordering is not alphabetical and not the config
//! file's order — providers with headroom come first, and a provider with
//! nothing left sinks to the bottom ranked by how soon it comes back.

use crate::model::{ProviderConfig, ProviderUsage};
use crate::time::countdown_seconds;

/// Providers pinned to the front, in this order. Everything else keeps its
/// existing relative position.
pub const DISPLAY_PRIORITY: [&str; 5] = ["claude", "grok", "antigravity", "codex", "opencode"];

/// Position in [`DISPLAY_PRIORITY`], or one past the end for anything unpinned.
fn priority_rank(id: &str) -> usize {
    DISPLAY_PRIORITY
        .iter()
        .position(|pinned| pinned.eq_ignore_ascii_case(id))
        .unwrap_or(DISPLAY_PRIORITY.len())
}

/// Order provider configs for display, before anything has been read.
pub fn sort_configs(configs: &mut [ProviderConfig]) {
    configs.sort_by_key(|config| priority_rank(&config.id));
}

/// Soonest reset first, with an unreadable countdown last.
fn soonest_reset(usage: &ProviderUsage) -> Option<i64> {
    usage
        .windows
        .iter()
        .filter_map(|window| countdown_seconds(&window.reset_countdown))
        .min()
}

/// Order readings: usable providers by plan tier, then spent ones by who
/// recovers soonest. Provider priority breaks ties.
pub fn sort_results(results: &mut [ProviderUsage]) {
    results.sort_by(|a, b| {
        let (a_spent, b_spent) = (a.is_exhausted(), b.is_exhausted());
        a_spent
            .cmp(&b_spent)
            .then_with(|| {
                if !a_spent {
                    let a_tier = a.plan.as_ref().map(|plan| plan.tier).unwrap_or_default();
                    let b_tier = b.plan.as_ref().map(|plan| plan.tier).unwrap_or_default();
                    return b_tier.cmp(&a_tier);
                }
                // A provider that says when it comes back is more useful than
                // one that does not, so an unknown reset sorts last.
                match (soonest_reset(a), soonest_reset(b)) {
                    (Some(a), Some(b)) => a.cmp(&b),
                    (Some(_), None) => std::cmp::Ordering::Less,
                    (None, Some(_)) => std::cmp::Ordering::Greater,
                    (None, None) => std::cmp::Ordering::Equal,
                }
            })
            .then_with(|| priority_rank(&a.id).cmp(&priority_rank(&b.id)))
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Provider, UsageWindow};

    fn usage(id: &str, windows: Vec<UsageWindow>) -> ProviderUsage {
        let mut usage = ProviderUsage::healthy(Provider::Unknown, windows, "");
        usage.id = id.to_string();
        usage
    }

    fn ids(results: &[ProviderUsage]) -> Vec<&str> {
        results.iter().map(|u| u.id.as_str()).collect()
    }

    #[test]
    fn spent_providers_sink_below_usable_ones() {
        let mut results = vec![
            usage(
                "ClAuDe",
                vec![
                    UsageWindow::new("Session", 28.0),
                    UsageWindow::new("Weekly", 49.0),
                ],
            ),
            usage(
                "GrOk",
                vec![UsageWindow::new("Weekly", 100.0).reset("3d 2h")],
            ),
            usage(
                "AnTiGrAvItY",
                vec![
                    UsageWindow::new("Session Gemini", 2.0),
                    UsageWindow::new("Weekly Gemini", 24.0),
                    UsageWindow::new("Weekly Claude & GPT", 100.0),
                ],
            ),
            usage(
                "CoDeX",
                vec![UsageWindow::new("Primary", 100.0).reset("1h 30m")],
            ),
            usage(
                "CoPiLoT",
                vec![
                    UsageWindow::new("Chat", 5.9),
                    UsageWindow::new("Completions", 0.0),
                ],
            ),
        ];
        sort_results(&mut results);

        assert_eq!(
            ids(&results),
            ["ClAuDe", "AnTiGrAvItY", "CoPiLoT", "CoDeX", "GrOk"]
        );
    }

    #[test]
    fn a_spent_provider_with_an_unknown_reset_sorts_last() {
        let mut results = vec![
            usage(
                "grok",
                vec![UsageWindow::new("Weekly", 100.0).reset("Unknown")],
            ),
            usage(
                "codex",
                vec![UsageWindow::new("Primary", 100.0).reset("Resets now")],
            ),
        ];
        sort_results(&mut results);

        assert_eq!(ids(&results), ["codex", "grok"]);
    }

    #[test]
    fn spent_providers_are_ranked_by_soonest_reset() {
        let mut results = vec![
            usage("a", vec![UsageWindow::new("W", 100.0).reset("3d 0h")]),
            usage("b", vec![UsageWindow::new("W", 100.0).reset("45m")]),
            usage("c", vec![UsageWindow::new("W", 100.0).reset("2h 0m")]),
        ];
        sort_results(&mut results);

        assert_eq!(ids(&results), ["b", "c", "a"]);
    }

    #[test]
    fn a_providers_soonest_window_is_the_one_that_ranks_it() {
        let mut results = vec![
            usage(
                "later",
                vec![
                    UsageWindow::new("W", 100.0).reset("5d 0h"),
                    UsageWindow::new("S", 100.0).reset("4h 0m"),
                ],
            ),
            usage("sooner", vec![UsageWindow::new("W", 100.0).reset("1h 0m")]),
        ];
        sort_results(&mut results);

        assert_eq!(ids(&results), ["sooner", "later"]);
    }

    #[test]
    fn usable_providers_put_larger_plans_before_provider_priority() {
        let mut results = vec![
            usage("claude", vec![UsageWindow::new("Session", 10.0)]).with_plan("free"),
            usage("codex", vec![UsageWindow::new("Session", 10.0)]).with_plan("prolite"),
            usage("grok", vec![UsageWindow::new("Weekly", 10.0)]).with_plan("SuperGrok Heavy"),
        ];
        sort_results(&mut results);
        assert_eq!(ids(&results), ["grok", "codex", "claude"]);
    }

    #[test]
    fn equal_plan_tiers_keep_provider_priority_and_stable_order() {
        let mut results = vec![
            usage("grok", vec![]).with_plan("SuperGrok"),
            usage("codex", vec![]).with_plan("prolite"),
            usage("claude", vec![]).with_plan("pro"),
            usage("second", vec![]),
            usage("first", vec![]),
        ];
        sort_results(&mut results);
        assert_eq!(
            ids(&results),
            ["claude", "grok", "codex", "second", "first"]
        );
    }

    #[test]
    fn plan_tiers_never_override_exhaustion_or_recovery_order() {
        let mut results = vec![
            usage(
                "claude",
                vec![UsageWindow::new("Weekly", 100.0).reset("3d 0h")],
            )
            .with_plan("max"),
            usage(
                "grok",
                vec![UsageWindow::new("Weekly", 100.0).reset("Unknown")],
            )
            .with_plan("SuperGrok Heavy"),
            usage(
                "codex",
                vec![UsageWindow::new("Weekly", 100.0).reset("1h 0m")],
            )
            .with_plan("prolite"),
            usage("copilot", vec![UsageWindow::new("Chat", 10.0)]).with_plan("free"),
        ];
        sort_results(&mut results);
        assert_eq!(ids(&results), ["copilot", "codex", "claude", "grok"]);

        let mut results = vec![
            usage(
                "grok",
                vec![UsageWindow::new("Weekly", 100.0).reset("1h 0m")],
            )
            .with_plan("SuperGrok Heavy"),
            usage(
                "claude",
                vec![UsageWindow::new("Weekly", 100.0).reset("1h 0m")],
            )
            .with_plan("pro"),
        ];
        sort_results(&mut results);
        assert_eq!(ids(&results), ["claude", "grok"]);
    }

    #[test]
    fn unknown_plans_keep_the_existing_order() {
        let mut results = vec![
            usage("codex", vec![]).with_plan("experimental"),
            usage("opencode", vec![]),
            usage("claude", vec![]).with_plan("maximum"),
            usage("grok", vec![]).with_plan("improved"),
        ];
        sort_results(&mut results);
        assert_eq!(ids(&results), ["claude", "grok", "codex", "opencode"]);
    }

    #[test]
    fn usable_providers_keep_the_pinned_order() {
        let mut results = vec![
            usage("copilot", vec![UsageWindow::new("Chat", 1.0)]),
            usage("opencode", vec![UsageWindow::new("Rolling", 1.0)]),
            usage("claude", vec![UsageWindow::new("Session", 1.0)]),
        ];
        sort_results(&mut results);

        assert_eq!(ids(&results), ["claude", "opencode", "copilot"]);
    }

    #[test]
    fn a_provider_reporting_nothing_is_not_treated_as_spent() {
        let mut results = vec![
            usage("spent", vec![UsageWindow::new("W", 100.0).reset("1h 0m")]),
            usage("silent", vec![]),
        ];
        sort_results(&mut results);

        assert_eq!(ids(&results), ["silent", "spent"]);
    }

    #[test]
    fn configs_are_ordered_by_the_pinned_list() {
        let mut configs: Vec<ProviderConfig> = ["copilot", "codex", "openai", "claude"]
            .iter()
            .map(|id| ProviderConfig::new(id, true))
            .collect();
        sort_configs(&mut configs);

        let order: Vec<&str> = configs.iter().map(|c| c.id.as_str()).collect();
        assert_eq!(order, ["claude", "codex", "copilot", "openai"]);
    }
}
