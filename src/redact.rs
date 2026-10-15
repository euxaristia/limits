//! Email masking for anything that can reach a terminal, a status bar, or a
//! JSON payload.
//!
//! Provider footers carry the signed-in account, and `limits --json` is
//! routinely piped into shell scripts and shared in bug reports. The local part
//! is reduced to its first and last character; the domain stays, because
//! knowing *which* account is signed in is the point of showing it at all.

/// Replace the local part of every email in `input` with `f***l`.
// PERF: Optimized email redaction by adding a fast path for inputs without `@`,
// avoiding String allocations when no emails exist, scanning bytes directly
// instead of decoding UTF-8 char indices, and formatting masked local parts
// directly into the output buffer without intermediate String allocations.
pub fn redact_emails(input: &str) -> String {
    if input.trim().is_empty() {
        return String::new();
    }

    let bytes = input.as_bytes();

    // Fast path: if there is no `@` byte, no email can be present.
    // In UTF-8, ASCII '@' (0x40) cannot appear as a byte in multi-byte character sequences.
    if !bytes.contains(&b'@') {
        return input.to_string();
    }

    let mut out: Option<String> = None;
    let mut copied = 0usize;

    // Scan for '@' byte positions directly rather than decoding UTF-8 chars.
    for at in 0..bytes.len() {
        if bytes[at] != b'@' || at < copied {
            continue;
        }
        let Some(local) = local_part(bytes, at) else {
            continue;
        };
        let Some(domain_end) = domain_part(bytes, at + 1) else {
            continue;
        };

        // Lazily allocate output buffer on the first matched email.
        let buf = out.get_or_insert_with(|| String::with_capacity(input.len()));
        buf.push_str(&input[copied..local]);
        push_masked_local(buf, &input[local..at]);
        buf.push_str(&input[at..domain_end]);
        copied = domain_end;
    }

    match out {
        Some(mut buf) => {
            buf.push_str(&input[copied..]);
            buf
        }
        None => input.to_string(),
    }
}

/// Append the masked local part directly to `out` without allocating intermediate strings.
/// Since `local_part` guarantees `local` consists solely of ASCII bytes, `local.len()` is
/// its character count.
fn push_masked_local(out: &mut String, local: &str) {
    let bytes = local.as_bytes();
    if bytes.is_empty() {
        return;
    }
    let first = bytes[0] as char;
    out.push(first);
    out.push_str("***");
    if bytes.len() >= 3 {
        let last = bytes[bytes.len() - 1] as char;
        out.push(last);
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
