## 2026-04-02 - TUI render loop precomputed display strings and fast-path override parsing
**Learning:** The TUI render loop ticks every 250ms. Previously, `draw_list` and `draw_windows` re-formatted remaining quota strings (`format!("{left:>5.1}% left")`), cloned window labels, and re-parsed override text on every frame. Precomputing `sidebar_label`, `sidebar_color`, `remaining_percent`, and `remaining_text` on `LiveProvider` and `LiveWindow` at fetch time (once per 60s poll) eliminates dozens of heap allocations per frame on the rendering path. Fast-pathing `remaining_percent` by checking for `%` before running string suffix checks avoids unnecessary string parsing when categorical overrides (e.g. `Unlimited`, `quota unavailable`) are present.
**Action:** Precompute static view model strings when model state updates rather than recomputing them inside high-frequency UI draw frames.

## 2026-04-01 - Zero-allocation provider resolution and single-pass plan normalization
**Learning:** `Provider::from` and `SubscriptionPlan::new` are invoked during configuration parsing, provider lookups, and snapshot generation. Matching on `value.to_ascii_lowercase().as_str()` allocates a heap `String` on every invocation. Replacing it with `eq_ignore_ascii_case` over `Provider::ALL` eliminates allocations entirely. Similarly, replacing `.to_ascii_lowercase().replace(...).split_whitespace().collect().join(...)` string pipelines in `SubscriptionPlan::new` with single-pass `String::with_capacity` loops eliminates 6+ intermediate heap allocations.
**Action:** Prefer `eq_ignore_ascii_case` over `to_ascii_lowercase()` for enum parsing from string slices, and use single-pass character iterators for string normalization/title-casing.

## 2026-03-31 - Fast-path & zero-allocation email redaction
**Learning:** `redact_emails` is invoked on provider footers and error messages during quota fetches. Most strings carry no email address or only ASCII local parts. Fast-pathing absence of `@` (`bytes.contains(&b'@')`), lazily allocating the output String, and formatting masked local parts directly into the output buffer eliminates string allocation and UTF-8 char decoding overhead.
**Action:** When performing string sanitization or redaction on hot paths, check for required trigger bytes first and stream output directly to avoid intermediate heap allocations.

## 2026-03-31 - TUI render loop zero-allocation sparkline and layout slicing
**Learning:** The TUI render loop ticks every 250ms. `sparkline` and `draw_windows` previously created heap-allocated `Vec` instances on every frame via `collect::<Vec<_>>()` and double iterator reversals. Slicing trailing points directly (`&points[len.saturating_sub(width)..]`) and passing iterators directly to `Layout::vertical` eliminates per-frame heap allocations on the UI render path.
**Action:** When writing TUI widget render routines, prefer slicing or iterator adapters directly over intermediate vector allocations.
