## 2026-03-31 - Fast-path & zero-allocation email redaction
**Learning:** `redact_emails` is invoked on provider footers and error messages during quota fetches. Most strings carry no email address or only ASCII local parts. Fast-pathing absence of `@` (`bytes.contains(&b'@')`), lazily allocating the output String, and formatting masked local parts directly into the output buffer eliminates string allocation and UTF-8 char decoding overhead.
**Action:** When performing string sanitization or redaction on hot paths, check for required trigger bytes first and stream output directly to avoid intermediate heap allocations.

## 2026-03-31 - TUI render loop zero-allocation sparkline and layout slicing
**Learning:** The TUI render loop ticks every 250ms. `sparkline` and `draw_windows` previously created heap-allocated `Vec` instances on every frame via `collect::<Vec<_>>()` and double iterator reversals. Slicing trailing points directly (`&points[len.saturating_sub(width)..]`) and passing iterators directly to `Layout::vertical` eliminates per-frame heap allocations on the UI render path.
**Action:** When writing TUI widget render routines, prefer slicing or iterator adapters directly over intermediate vector allocations.
