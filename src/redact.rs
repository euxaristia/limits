//! Email masking for anything that can reach a terminal, a status bar, or a
//! JSON payload.
//!
//! Provider footers carry the signed-in account, and `limits --json` is
//! routinely piped into shell scripts and shared in bug reports. The local part
//! is reduced to its first and last character; the domain stays, because
//! knowing *which* account is signed in is the point of showing it at all.

/// Replace the local part of every email in `input` with `f***l`.
pub fn redact_emails(input: &str) -> String {
    if input.trim().is_empty() {
        return String::new();
    }

    let bytes = input.as_bytes();
    let mut out = String::with_capacity(input.len());
    let mut copied = 0usize;

    for (at, _) in input.char_indices().filter(|(_, c)| *c == '@') {
        if at < copied {
            continue;
        }
        let Some(local) = local_part(bytes, at) else {
            continue;
        };
        let Some(domain_end) = domain_part(bytes, at + 1) else {
            continue;
        };

        out.push_str(&input[copied..local]);
        out.push_str(&mask(&input[local..at]));
        out.push_str(&input[at..domain_end]);
        copied = domain_end;
    }

    out.push_str(&input[copied..]);
    out
}

fn mask(local: &str) -> String {
    let mut chars = local.chars();
    let first = chars.next().unwrap_or('*');
    match local.chars().count() {
        0 => String::new(),
        1 | 2 => format!("{first}***"),
        _ => format!("{first}***{}", local.chars().next_back().unwrap_or('*')),
    }
}

/// Walk back from `@` over the local part, returning where it starts. Mirrors
/// the `[a-zA-Z0-9._%+-]+` half of the address grammar, anchored on a word
/// boundary so `foo.bar@x.com` keeps its dots but `(user@x.com` drops the paren.
fn local_part(bytes: &[u8], at: usize) -> Option<usize> {
    let mut start = at;
    while start > 0 && is_local_byte(bytes[start - 1]) {
        start -= 1;
    }
    (start < at).then_some(start)
}

/// Walk forward over `host.tld`, returning where it ends. A domain needs at
/// least one dot and a two-letter-or-longer final label, which is what keeps
/// `@here` and `@1.2` from being treated as addresses.
fn domain_part(bytes: &[u8], start: usize) -> Option<usize> {
    let mut end = start;
    while end < bytes.len()
        && (bytes[end].is_ascii_alphanumeric() || matches!(bytes[end], b'.' | b'-'))
    {
        end += 1;
    }
    // Trailing punctuation belongs to the sentence, not the address.
    while end > start && matches!(bytes[end - 1], b'.' | b'-') {
        end -= 1;
    }
    let domain = &bytes[start..end];
    let last_dot = domain.iter().rposition(|b| *b == b'.')?;
    let tld = &domain[last_dot + 1..];
    (tld.len() >= 2 && tld.iter().all(u8::is_ascii_alphabetic)).then_some(end)
}

fn is_local_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'%' | b'+' | b'-')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn masks_the_local_part_and_keeps_the_domain() {
        assert_eq!(
            redact_emails("Grok CLI (abcdefghij@example.com)"),
            "Grok CLI (a***j@example.com)"
        );
        assert_eq!(
            redact_emails("Account: user.name@example.com"),
            "Account: u***e@example.com"
        );
    }

    #[test]
    fn masks_short_local_parts_without_revealing_them() {
        assert_eq!(redact_emails("ab@example.com"), "a***@example.com");
        assert_eq!(redact_emails("a@example.com"), "a***@example.com");
    }

    #[test]
    fn masks_every_address_in_one_string() {
        assert_eq!(
            redact_emails("first@one.com and second@two.org"),
            "f***t@one.com and s***d@two.org"
        );
    }

    #[test]
    fn leaves_non_addresses_alone() {
        assert_eq!(redact_emails(""), "");
        assert_eq!(redact_emails("   "), "");
        assert_eq!(redact_emails("no address here"), "no address here");
        assert_eq!(redact_emails("@handle"), "@handle");
        assert_eq!(redact_emails("user@localhost"), "user@localhost");
    }

    #[test]
    fn drops_trailing_punctuation_from_the_domain() {
        assert_eq!(
            redact_emails("mail someone@example.com."),
            "mail s***e@example.com."
        );
    }

    #[test]
    fn handles_multibyte_text_around_an_address() {
        assert_eq!(
            redact_emails("plan · someone@example.com · ok"),
            "plan · s***e@example.com · ok"
        );
    }
}
