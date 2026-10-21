## 2026-04-02 - TUI render loop string reference and compile-time constant optimization
**Learning:** The TUI dashboard redrawing loop fires every 250ms (4 FPS). Dynamic `format!` calls for static application versions, title blocks, and footer/error lines created ~7 intermediate heap `String` allocations per frame. Utilizing compile-time `concat!`, borrowing string slices (`&str`) directly into Ratatui `Span`s, and combining nested countdown `format!` blocks into single spans eliminates all heap string allocations across the TUI rendering pipeline.
**Action:** In high-frequency UI render loops, compose Ratatui `Line`s and `Title`s from borrowed `Span` references and static strings rather than allocating formatted `String`s.

## 2026-04-01 - Zero-allocation provider resolution and single-pass plan normalization
**Learning:** `Provider::from` and `SubscriptionPlan::new` are invoked during configuration parsing, provider lookups, and snapshot generation. Matching on `value.to_ascii_lowercase().as_str()` allocates a heap `String` on every invocation. Replacing it with `eq_ignore_ascii_case` over `Provider::ALL` eliminates allocations entirely. Similarly, replacing `.to_ascii_lowercase().replace(...).split_whitespace().collect().join(...)` string pipelines in `SubscriptionPlan::new` with single-pass `String::with_capacity` loops eliminates 6+ intermediate heap allocations.
**Action:** Prefer `eq_ignore_ascii_case` over `to_ascii_lowercase()` for enum parsing from string slices, and use single-pass character iterators for string normalization/title-casing.

## 2026-03-31 - Fast-path & zero-allocation email redaction
**Learning:** `redact_emails` is invoked on provider footers and error messages during quota fetches. Most strings carry no email address or only ASCII local parts. Fast-pathing absence of `@` (`bytes.contains(&b'@')`), lazily allocating the output String, and formatting masked local parts directly into the output buffer eliminates string allocation and UTF-8 char decoding overhead.
**Action:** When performing string sanitization or redaction on hot paths, check for required trigger bytes first and stream output directly to avoid intermediate heap allocations.

## 2026-03-31 - TUI render loop zero-allocation sparkline and layout slicing
**Learning:** The TUI render loop ticks every 250ms. `sparkline` and `draw_windows` previously created heap-allocated `Vec` instances on every frame via `collect::<Vec<_>>()` and double iterator reversals. Slicing trailing points directly (`&points[len.saturating_sub(width)..]`) and passing iterators directly to `Layout::vertical` eliminates per-frame heap allocations on the UI render path.
**Action:** When writing TUI widget render routines, prefer slicing or iterator adapters directly over intermediate vector allocations.
