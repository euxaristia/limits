## 2026-04-01 - TUI frame render path micro-allocation elimination
**Learning:** The TUI dashboard ticks every 250ms (4 times per second). In `draw`, `app.visible()` filtered providers and allocated a new `Vec<&LiveProvider>` twice per frame (once in `draw` and again in `draw_detail` via `selected_provider()`). Reusing the pre-computed `&visible` slice via `selected_provider_from` eliminates per-frame vector allocations. In addition, replacing `format!(" limits v{VERSION} ")` with `concat!(" limits v", env!("CARGO_PKG_VERSION"), " ")` and streamlining `draw_windows` countdown formatting removes multiple `String` allocations per window on every tick.
**Action:** Pass pre-computed slices across render helpers instead of re-evaluating filtering methods on hot UI render paths, and use static `concat!` string slices for compile-time constants.

## 2026-04-01 - Zero-allocation provider resolution and single-pass plan normalization
**Learning:** `Provider::from` and `SubscriptionPlan::new` are invoked during configuration parsing, provider lookups, and snapshot generation. Matching on `value.to_ascii_lowercase().as_str()` allocates a heap `String` on every invocation. Replacing it with `eq_ignore_ascii_case` over `Provider::ALL` eliminates allocations entirely. Similarly, replacing `.to_ascii_lowercase().replace(...).split_whitespace().collect().join(...)` string pipelines in `SubscriptionPlan::new` with single-pass `String::with_capacity` loops eliminates 6+ intermediate heap allocations.
**Action:** Prefer `eq_ignore_ascii_case` over `to_ascii_lowercase()` for enum parsing from string slices, and use single-pass character iterators for string normalization/title-casing.

## 2026-03-31 - Fast-path & zero-allocation email redaction
**Learning:** `redact_emails` is invoked on provider footers and error messages during quota fetches. Most strings carry no email address or only ASCII local parts. Fast-pathing absence of `@` (`bytes.contains(&b'@')`), lazily allocating the output String, and formatting masked local parts directly into the output buffer eliminates string allocation and UTF-8 char decoding overhead.
**Action:** When performing string sanitization or redaction on hot paths, check for required trigger bytes first and stream output directly to avoid intermediate heap allocations.

## 2026-03-31 - TUI render loop zero-allocation sparkline and layout slicing
**Learning:** The TUI render loop ticks every 250ms. `sparkline` and `draw_windows` previously created heap-allocated `Vec` instances on every frame via `collect::<Vec<_>>()` and double iterator reversals. Slicing trailing points directly (`&points[len.saturating_sub(width)..]`) and passing iterators directly to `Layout::vertical` eliminates per-frame heap allocations on the UI render path.
**Action:** When writing TUI widget render routines, prefer slicing or iterator adapters directly over intermediate vector allocations.
