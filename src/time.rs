//! Timestamps and countdowns, without a date library.
//!
//! Everything here works in Unix seconds. Providers state resets as RFC 3339
//! strings, Unix seconds, or Unix milliseconds; a person wants "3d 2h"; and the
//! display sort needs to turn that back into a number to rank exhausted
//! providers by who recovers first. Those four conversions are the whole
//! module.

use std::time::{SystemTime, UNIX_EPOCH};

pub const MINUTE: i64 = 60;
pub const HOUR: i64 = 60 * MINUTE;
pub const DAY: i64 = 24 * HOUR;
pub const WEEK: i64 = 7 * DAY;

/// Wall clock as Unix seconds.
pub fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Parse an RFC 3339 timestamp into Unix seconds.
///
/// Accepts a trailing `Z`, a numeric `±HH:MM` / `±HHMM` offset, or no zone at
/// all (read as UTC, which is what the previous implementation did for the
/// naive form some providers emit). Fractional seconds are accepted and
/// truncated: nothing here is measured finer than a second.
pub fn parse_rfc3339(input: &str) -> Option<i64> {
    let s = input.trim();
    let bytes = s.as_bytes();
    if bytes.len() < 19 {
        return None;
    }

    let year: i64 = s.get(0..4)?.parse().ok()?;
    let month: i64 = s.get(5..7)?.parse().ok()?;
    let day: i64 = s.get(8..10)?.parse().ok()?;
    let hour: i64 = s.get(11..13)?.parse().ok()?;
    let minute: i64 = s.get(14..16)?.parse().ok()?;
    let second: i64 = s.get(17..19)?.parse().ok()?;

    if bytes[4] != b'-'
        || bytes[7] != b'-'
        || !matches!(bytes[10], b'T' | b't' | b' ')
        || bytes[13] != b':'
        || bytes[16] != b':'
    {
        return None;
    }
    if !(1..=12).contains(&month)
        || !(1..=31).contains(&day)
        || hour > 23
        || minute > 59
        // A leap second lands at :60 and must not sink the whole reading.
        || second > 60
    {
        return None;
    }

    let mut rest = &s[19..];
    if let Some(fraction) = rest.strip_prefix('.') {
        let digits = fraction.bytes().take_while(u8::is_ascii_digit).count();
        rest = &fraction[digits..];
    }

    let offset = parse_offset(rest)?;
    let days = days_from_civil(year, month, day);
    Some(days * DAY + hour * HOUR + minute * MINUTE + second - offset)
}

/// Seconds to add to a local time to reach UTC, i.e. the negation of the zone
/// offset. `None` means the suffix is not a zone at all.
fn parse_offset(rest: &str) -> Option<i64> {
    if rest.is_empty() || rest.eq_ignore_ascii_case("z") {
        return Some(0);
    }
    let (sign, body) = match rest.as_bytes()[0] {
        b'+' => (1, &rest[1..]),
        b'-' => (-1, &rest[1..]),
        _ => return None,
    };
    let digits: String = body.chars().filter(|c| *c != ':').collect();
    if digits.len() != 4 || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let hours: i64 = digits[0..2].parse().ok()?;
    let minutes: i64 = digits[2..4].parse().ok()?;
    if hours > 23 || minutes > 59 {
        return None;
    }
    Some(sign * (hours * HOUR + minutes * MINUTE))
}

/// Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's
/// `days_from_civil`). Correct for any year, including the century leap rules
/// that a naive 365.25 approximation gets wrong.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let year_of_era = y - era * 400;
    let month_shift = if month > 2 { month - 3 } else { month + 9 };
    let day_of_year = (153 * month_shift + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// Read a JSON numeric timestamp that may be in seconds or milliseconds.
/// Providers disagree, and the two are three orders of magnitude apart, so the
/// magnitude itself is the discriminator.
pub fn unix_from_number(value: f64) -> i64 {
    let raw = value as i64;
    if raw.abs() > 100_000_000_000 {
        raw / 1000
    } else {
        raw
    }
}

/// Time from `now` until `target`, as `3d 2h` / `2h 5m` / `45m`.
pub fn countdown_between(target: i64, now: i64) -> String {
    let seconds = target - now;
    if seconds <= 0 {
        return "Resets now".to_string();
    }
    let hours = seconds / HOUR;
    let minutes = (seconds % HOUR) / MINUTE;
    if hours >= 24 {
        format!("{}d {}h", hours / 24, hours % 24)
    } else if hours > 0 {
        format!("{hours}h {minutes}m")
    } else {
        format!("{minutes}m")
    }
}

/// Countdown to an RFC 3339 instant, or `Unknown` when it cannot be read.
pub fn format_countdown(timestamp: &str) -> String {
    match parse_rfc3339(timestamp) {
        Some(target) => countdown_between(target, now_unix()),
        None => "Unknown".to_string(),
    }
}

/// Format a duration in seconds as a countdown string (e.g. `5h 0m`, `7d 0h`, `45m`).
pub fn format_duration(seconds: i64) -> String {
    countdown_between(seconds, 0)
}

/// Turn a countdown string back into seconds.
///
/// Used only for ordering exhausted providers by who frees up first, so an
/// unreadable countdown is `None` (sorts last) rather than a guess. Rejects
/// arithmetic overflow instead of wrapping into a small number, which would
/// promote a nonsense value to the front of the list.
pub fn countdown_seconds(text: &str) -> Option<i64> {
    let text = text.trim();
    if text.eq_ignore_ascii_case("Resets now") {
        return Some(0);
    }
    let mut fields = text.split_whitespace().peekable();
    fields.peek()?;

    let mut total: i64 = 0;
    for field in fields {
        let (value, unit) = field.split_at(field.len().checked_sub(1)?);
        let value: i64 = value.parse().ok()?;
        if value < 0 {
            return None;
        }
        let unit_seconds = match unit {
            "d" => DAY,
            "h" => HOUR,
            "m" => MINUTE,
            "s" => 1,
            _ => return None,
        };
        total = value.checked_mul(unit_seconds)?.checked_add(total)?;
    }
    Some(total)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_utc_timestamps() {
        assert_eq!(parse_rfc3339("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(parse_rfc3339("2026-08-14T21:01:47Z"), Some(1_786_741_307));
    }

    #[test]
    fn parses_fractional_seconds_as_opencode_sends_them() {
        assert_eq!(
            parse_rfc3339("2026-08-14T21:01:47.112Z"),
            parse_rfc3339("2026-08-14T21:01:47Z")
        );
    }

    #[test]
    fn applies_numeric_zone_offsets() {
        let utc = parse_rfc3339("2026-08-14T12:00:00Z").unwrap();
        assert_eq!(parse_rfc3339("2026-08-14T14:00:00+02:00"), Some(utc));
        assert_eq!(parse_rfc3339("2026-08-14T07:00:00-05:00"), Some(utc));
        assert_eq!(parse_rfc3339("2026-08-14T14:00:00+0200"), Some(utc));
    }

    #[test]
    fn reads_a_zoneless_timestamp_as_utc() {
        assert_eq!(
            parse_rfc3339("2026-08-14T12:00:00"),
            parse_rfc3339("2026-08-14T12:00:00Z")
        );
    }

    #[test]
    fn rejects_malformed_timestamps() {
        for bad in [
            "",
            "not a time",
            "2026-08-14",
            "2026/08/14T12:00:00Z",
            "2026-13-01T00:00:00Z",
            "2026-08-14T25:00:00Z",
            "2026-08-14T12:00:00+99:00",
        ] {
            assert_eq!(parse_rfc3339(bad), None, "{bad} should not parse");
        }
    }

    #[test]
    fn handles_leap_years_and_century_rules() {
        // 2000 was a leap year, 1900 was not; a 365.25-day approximation gets
        // one of these wrong.
        assert_eq!(
            parse_rfc3339("2000-03-01T00:00:00Z").unwrap()
                - parse_rfc3339("2000-02-28T00:00:00Z").unwrap(),
            2 * DAY
        );
        assert_eq!(
            parse_rfc3339("1900-03-01T00:00:00Z").unwrap()
                - parse_rfc3339("1900-02-28T00:00:00Z").unwrap(),
            DAY
        );
    }

    #[test]
    fn formats_countdowns_by_magnitude() {
        assert_eq!(countdown_between(100, 100), "Resets now");
        assert_eq!(countdown_between(50, 100), "Resets now");
        assert_eq!(countdown_between(45 * MINUTE, 0), "45m");
        assert_eq!(countdown_between(2 * HOUR + 5 * MINUTE, 0), "2h 5m");
        assert_eq!(countdown_between(DAY, 0), "1d 0h");
        assert_eq!(countdown_between(3 * DAY + 2 * HOUR, 0), "3d 2h");
    }

    #[test]
    fn unknown_timestamps_do_not_pretend_to_be_countdowns() {
        assert_eq!(format_countdown("nonsense"), "Unknown");
    }

    #[test]
    fn distinguishes_second_and_millisecond_timestamps() {
        assert_eq!(unix_from_number(1_700_000_000.0), 1_700_000_000);
        assert_eq!(unix_from_number(1_700_000_000_000.0), 1_700_000_000);
    }

    #[test]
    fn reads_countdown_text_back_into_seconds() {
        assert_eq!(countdown_seconds("Resets now"), Some(0));
        assert_eq!(countdown_seconds("45m"), Some(45 * MINUTE));
        assert_eq!(countdown_seconds("2h 5m"), Some(2 * HOUR + 5 * MINUTE));
        assert_eq!(countdown_seconds("3d 2h"), Some(3 * DAY + 2 * HOUR));
        assert_eq!(countdown_seconds("Unknown"), None);
        assert_eq!(countdown_seconds(""), None);
        assert_eq!(countdown_seconds("5x"), None);
        assert_eq!(countdown_seconds("-1h"), None);
    }

    #[test]
    fn rejects_countdown_overflow_rather_than_wrapping() {
        assert_eq!(countdown_seconds(&format!("{}s", i64::MAX)), Some(i64::MAX));
        for unit in ["d", "h", "m"] {
            assert_eq!(
                countdown_seconds(&format!("{}{unit}", i64::MAX)),
                None,
                "{unit} conversion should overflow"
            );
        }
        assert_eq!(countdown_seconds(&format!("{}s 1s", i64::MAX)), None);
    }
}
