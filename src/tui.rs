//! The full-screen dashboard.
//!
//! A quota report read once tells you where you stood a moment ago. This keeps
//! it current: a worker thread re-reads the providers on a timer while the
//! render loop redraws several times a second, so countdowns tick down between
//! polls and a change in utilisation shows up as movement rather than as a
//! number that was different last time you looked.
//!
//! Reading happens off the event loop, always. Each provider read is a blocking
//! network call, and a provider that stops answering would otherwise freeze
//! every keystroke for the whole timeout.

use crate::model::{ProviderUsage, Status, UsageWindow};
use crate::time::{countdown_between, countdown_seconds, now_unix};
use crate::{Limits, VERSION};
use ratatui::crossterm::event::{self, Event, KeyCode, KeyEventKind, KeyModifiers};
use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Clear, Gauge, Paragraph};
use ratatui::{Frame, TerminalOptions, Viewport};
use std::collections::HashMap;
use std::sync::mpsc::{Receiver, Sender};
use std::time::{Duration, Instant};

/// How often the screen is redrawn. Fast enough that a countdown ticking from
/// `2m` to `1m` looks immediate, slow enough to stay invisible in a CPU graph.
const FRAME: Duration = Duration::from_millis(250);
/// Default seconds between reads. Quota endpoints are not free and the numbers
/// move on the scale of minutes.
const DEFAULT_INTERVAL: u64 = 60;
const MIN_INTERVAL: u64 = 5;
const MAX_INTERVAL: u64 = 3600;
/// Readings kept per provider for the trend line.
const HISTORY: usize = 48;

/// Utilisation at which a bar turns amber, then red.
const WARN_AT: f64 = 75.0;
const CRITICAL_AT: f64 = 90.0;

// ---- live values ---------------------------------------------------------

/// A window plus the instant its countdown was anchored to.
///
/// The provider states a countdown, not a deadline. Turning it into a deadline
/// once, at fetch time, is what lets the display tick every second without
/// re-reading anything.
#[derive(Clone, Debug)]
struct LiveWindow {
    window: UsageWindow,
    resets_at: Option<i64>,
}

impl LiveWindow {
    fn new(window: UsageWindow, fetched_at: i64) -> Self {
        let resets_at = countdown_seconds(&window.reset_countdown).map(|s| fetched_at + s);
        LiveWindow { window, resets_at }
    }

    /// The countdown as of now, not as of the last poll.
    fn countdown(&self, now: i64) -> String {
        match self.resets_at {
            Some(at) => countdown_between(at, now),
            None => self.window.reset_countdown.clone(),
        }
    }
}

#[derive(Clone, Debug)]
struct LiveProvider {
    usage: ProviderUsage,
    windows: Vec<LiveWindow>,
}

impl LiveProvider {
    fn new(usage: ProviderUsage, fetched_at: i64) -> Self {
        let windows = usage
            .windows
            .iter()
            .cloned()
            .map(|w| LiveWindow::new(w, fetched_at))
            .collect();
        LiveProvider { usage, windows }
    }
}

// ---- worker --------------------------------------------------------------

/// Reads providers on demand, off the event loop.
struct Worker {
    requests: Sender<()>,
    readings: Receiver<Vec<ProviderUsage>>,
}

impl Worker {
    fn spawn(provider_filter: Option<String>) -> Self {
        let (request_tx, request_rx) = std::sync::mpsc::channel::<()>();
        let (reading_tx, reading_rx) = std::sync::mpsc::channel::<Vec<ProviderUsage>>();

        std::thread::spawn(move || {
            // Built inside the thread so a slow config read never delays the
            // first frame.
            let limits = Limits::new();
            // Ends when the dashboard drops the sender, which is what stops
            // this thread on quit.
            while request_rx.recv().is_ok() {
                let results = limits.snapshot_filtered(|config| {
                    provider_filter
                        .as_deref()
                        .is_none_or(|id| config.id.eq_ignore_ascii_case(id))
                });
                if reading_tx.send(results).is_err() {
                    return;
                }
            }
        });

        Worker {
            requests: request_tx,
            readings: reading_rx,
        }
    }

    fn request(&self) {
        let _ = self.requests.send(());
    }

    /// The latest reading, if one has arrived. A disconnected worker is the
    /// same as no reading: the dashboard keeps showing what it last had.
    fn poll(&self) -> Option<Vec<ProviderUsage>> {
        self.readings.try_recv().ok()
    }
}

// ---- application state ---------------------------------------------------

struct App {
    providers: Vec<LiveProvider>,
    /// Recent utilisation per provider id, oldest first, for the trend line.
    history: HashMap<String, Vec<f64>>,
    selected: usize,
    show_all: bool,
    interval: Duration,
    last_read: Option<Instant>,
    reading: bool,
    message: Option<String>,
    started: Instant,
}

impl App {
    fn new(interval: u64) -> Self {
        App {
            providers: Vec::new(),
            history: HashMap::new(),
            selected: 0,
            show_all: false,
            interval: Duration::from_secs(interval.clamp(MIN_INTERVAL, MAX_INTERVAL)),
            last_read: None,
            reading: true,
            message: None,
            started: Instant::now(),
        }
    }

    /// Providers currently on screen. Errors are hidden until asked for, so a
    /// dozen unconfigured entries do not bury the two that report numbers.
    fn visible(&self) -> Vec<&LiveProvider> {
        self.providers
            .iter()
            .filter(|p| self.show_all || (p.usage.status == Status::Healthy && !p.usage.has_error))
            .collect()
    }

    fn selected_provider(&self) -> Option<&LiveProvider> {
        let visible = self.visible();
        visible
            .get(self.selected.min(visible.len().saturating_sub(1)))
            .copied()
    }

    fn apply(&mut self, results: Vec<ProviderUsage>) {
        let fetched_at = now_unix();
        for usage in &results {
            // A provider that reported nothing contributes no point, so a
            // transient failure does not draw a cliff into the trend.
            if usage.status == Status::Healthy && !usage.windows.is_empty() {
                let points = self.history.entry(usage.id.clone()).or_default();
                points.push(usage.peak_percent());
                if points.len() > HISTORY {
                    points.remove(0);
                }
            }
        }
        self.providers = results
            .into_iter()
            .map(|usage| LiveProvider::new(usage, fetched_at))
            .collect();
        self.reading = false;
        self.last_read = Some(Instant::now());
        self.clamp_selection();
    }

    fn clamp_selection(&mut self) {
        let count = self.visible().len();
        self.selected = match count {
            0 => 0,
            n => self.selected.min(n - 1),
        };
    }

    fn move_selection(&mut self, delta: isize) {
        let count = self.visible().len();
        if count == 0 {
            return;
        }
        let next = self.selected as isize + delta;
        self.selected = next.rem_euclid(count as isize) as usize;
    }

    fn adjust_interval(&mut self, delta: i64) {
        let seconds = (self.interval.as_secs() as i64 + delta)
            .clamp(MIN_INTERVAL as i64, MAX_INTERVAL as i64);
        self.interval = Duration::from_secs(seconds as u64);
        self.message = Some(format!("Refreshing every {seconds}s"));
    }

    /// Seconds until the next automatic read.
    fn until_refresh(&self) -> u64 {
        match self.last_read {
            Some(last) => self.interval.saturating_sub(last.elapsed()).as_secs(),
            None => 0,
        }
    }

    fn due_for_read(&self) -> bool {
        !self.reading
            && self
                .last_read
                .is_none_or(|last| last.elapsed() >= self.interval)
    }
}

// ---- rendering -----------------------------------------------------------

fn severity(percent: f64) -> Color {
    if percent >= CRITICAL_AT {
        Color::Red
    } else if percent >= WARN_AT {
        Color::Yellow
    } else {
        Color::Green
    }
}

fn dim() -> Style {
    Style::new().add_modifier(Modifier::DIM)
}

fn draw(frame: &mut Frame, app: &App) {
    frame.render_widget(Clear, frame.area());

    let [header, body, footer] = Layout::vertical([
        Constraint::Length(1),
        Constraint::Fill(1),
        Constraint::Length(1),
    ])
    .areas(frame.area());

    draw_header(frame, header, app);

    let visible = app.visible();
    if visible.is_empty() {
        draw_empty(frame, body, app);
    } else {
        // The list needs room for the longest provider name and its
        // percentage; the rest goes to the detail pane, which has bars in it.
        let list_width = visible
            .iter()
            .map(|p| p.usage.display_name.chars().count())
            .max()
            .unwrap_or(12)
            .clamp(12, 28) as u16
            + 12;
        let [list, detail] =
            Layout::horizontal([Constraint::Length(list_width), Constraint::Fill(1)]).areas(body);
        draw_list(frame, list, app, &visible);
        draw_detail(frame, detail, app);
    }

    draw_footer(frame, footer, app);
}

fn draw_header(frame: &mut Frame, area: Rect, app: &App) {
    let state = if app.reading {
        Span::styled(" reading… ", Style::new().fg(Color::Cyan))
    } else if app.until_refresh() == 0 {
        Span::styled(" due ", dim())
    } else {
        Span::styled(format!(" next in {}s ", app.until_refresh()), dim())
    };

    let elapsed = app.started.elapsed().as_secs();
    let line = Line::from(vec![
        Span::styled(
            format!(" limits v{VERSION} "),
            Style::new()
                .fg(Color::Black)
                .bg(Color::Cyan)
                .add_modifier(Modifier::BOLD),
        ),
        state,
        Span::styled(
            format!(
                "· {} provider{} · watching {}",
                app.providers.len(),
                if app.providers.len() == 1 { "" } else { "s" },
                format_elapsed(elapsed)
            ),
            dim(),
        ),
    ]);
    frame.render_widget(Paragraph::new(line), area);
}

fn draw_empty(frame: &mut Frame, area: Rect, app: &App) {
    let message = if app.reading {
        "Reading providers…"
    } else if app.providers.is_empty() {
        "No providers enabled. Run 'limits providers' to see what is available."
    } else {
        "No provider reported usable quota. Press 'a' to show errors."
    };
    frame.render_widget(
        Paragraph::new(Line::styled(message, dim())).block(Block::bordered()),
        area,
    );
}

fn draw_list(frame: &mut Frame, area: Rect, app: &App, visible: &[&LiveProvider]) {
    let block = Block::bordered().title(" Providers ");
    let inner = block.inner(area);
    frame.render_widget(block, area);

    let lines: Vec<Line> = visible
        .iter()
        .enumerate()
        .map(|(index, provider)| {
            let selected = index == app.selected;
            let percent = provider.usage.peak_percent();
            let marker = if selected { "\u{25b8} " } else { "  " };

            let (label, colour) = match provider.usage.status {
                Status::Healthy if provider.usage.windows.is_empty() => {
                    ("  --".to_string(), Color::DarkGray)
                }
                Status::Healthy => (format!("{percent:>4.0}%"), severity(percent)),
                Status::Degraded => ("  err".to_string(), Color::Yellow),
                Status::Unconfigured => ("   --".to_string(), Color::DarkGray),
            };

            let mut name = Style::new();
            if selected {
                name = name.add_modifier(Modifier::BOLD);
            }
            if provider.usage.is_exhausted() {
                name = name.fg(Color::DarkGray);
            }

            Line::from(vec![
                Span::styled(marker, Style::new().fg(Color::Cyan)),
                Span::styled(format!("{:<w$}", provider.usage.display_name, w = 16), name),
                Span::styled(label, Style::new().fg(colour)),
            ])
        })
        .collect();

    frame.render_widget(Paragraph::new(lines), inner);
}

fn draw_detail(frame: &mut Frame, area: Rect, app: &App) {
    let Some(provider) = app.selected_provider() else {
        return;
    };
    let usage = &provider.usage;

    let block = Block::bordered().title(format!(" {} ", usage.display_name));
    let inner = block.inner(area);
    frame.render_widget(block, area);
    if inner.height == 0 {
        return;
    }

    if usage.has_error {
        let colour = match usage.status {
            Status::Unconfigured => Color::DarkGray,
            _ => Color::Yellow,
        };
        frame.render_widget(
            Paragraph::new(Line::styled(
                usage.error_message.clone(),
                Style::new().fg(colour),
            )),
            inner,
        );
        return;
    }

    // Two rows per window (label line, then the bar), then the trend and the
    // footer.
    let window_rows = (usage.windows.len() as u16) * 2;
    let [windows_area, rest] = Layout::vertical([
        Constraint::Length(window_rows.min(inner.height)),
        Constraint::Fill(1),
    ])
    .areas(inner);

    draw_windows(frame, windows_area, provider);

    if rest.height == 0 {
        return;
    }
    let [trend, footer] =
        Layout::vertical([Constraint::Length(rest.height.min(2)), Constraint::Fill(1)]).areas(rest);
    draw_trend(frame, trend, app, &usage.id);

    if footer.height > 0 && !usage.footer.trim().is_empty() {
        frame.render_widget(
            Paragraph::new(Line::styled(usage.footer.clone(), dim())),
            footer,
        );
    }
}

fn draw_windows(frame: &mut Frame, area: Rect, provider: &LiveProvider) {
    if area.height == 0 {
        return;
    }
    let now = now_unix();
    let rows = Layout::vertical(
        (0..area.height)
            .map(|_| Constraint::Length(1))
            .collect::<Vec<_>>(),
    )
    .split(area);

    for (index, window) in provider.windows.iter().enumerate() {
        let label_row = index * 2;
        let bar_row = label_row + 1;
        if bar_row >= rows.len() {
            break;
        }

        let countdown = window.countdown(now);
        let reset = if countdown.is_empty() {
            String::new()
        } else {
            format!("resets {countdown}")
        };
        frame.render_widget(
            Paragraph::new(Line::from(vec![
                Span::styled(
                    window.window.label.clone(),
                    Style::new().add_modifier(Modifier::BOLD),
                ),
                Span::raw("  "),
                Span::styled(
                    window.window.percent_text(),
                    Style::new().fg(severity(window.window.used_percent)),
                ),
                Span::styled(format!("   {reset}"), dim()),
            ])),
            rows[label_row],
        );

        // `ratio` rather than `percent` so a bar does not snap to whole
        // percentage points as the number creeps.
        frame.render_widget(
            Gauge::default()
                .ratio((window.window.used_percent / 100.0).clamp(0.0, 1.0))
                .label("")
                .gauge_style(Style::new().fg(severity(window.window.used_percent)))
                .style(dim()),
            rows[bar_row],
        );
    }
}

/// A trend line of the readings taken so far this session.
fn draw_trend(frame: &mut Frame, area: Rect, app: &App, id: &str) {
    if area.height == 0 {
        return;
    }
    let points = app.history.get(id).map(Vec::as_slice).unwrap_or(&[]);
    let line = if points.len() < 2 {
        Line::styled("trend  (collecting…)", dim())
    } else {
        Line::from(vec![
            Span::styled("trend  ", dim()),
            Span::styled(
                sparkline(points, area.width.saturating_sub(8) as usize),
                Style::new().fg(severity(*points.last().unwrap_or(&0.0))),
            ),
        ])
    };
    frame.render_widget(Paragraph::new(line), area);
}

/// Render percentages as block characters.
///
/// The scale is fixed at 0-100 rather than fitted to the data: an
/// auto-scaled spark makes a drift from 40% to 41% look like a cliff.
fn sparkline(points: &[f64], width: usize) -> String {
    const BLOCKS: [char; 8] = [
        '\u{2581}', '\u{2582}', '\u{2583}', '\u{2584}', '\u{2585}', '\u{2586}', '\u{2587}',
        '\u{2588}',
    ];
    if width == 0 {
        return String::new();
    }
    points
        .iter()
        .rev()
        .take(width)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .map(|percent| {
            let index = ((percent.clamp(0.0, 100.0) / 100.0) * (BLOCKS.len() - 1) as f64).round();
            BLOCKS[index as usize]
        })
        .collect()
}

fn draw_footer(frame: &mut Frame, area: Rect, app: &App) {
    if let Some(message) = &app.message {
        frame.render_widget(
            Paragraph::new(Line::styled(message.clone(), Style::new().fg(Color::Cyan))),
            area,
        );
        return;
    }

    let keys = [
        ("q", "quit"),
        ("r", "refresh"),
        ("\u{2191}\u{2193}", "select"),
        (
            "a",
            if app.show_all {
                "hide errors"
            } else {
                "show all"
            },
        ),
        ("+/-", "interval"),
    ];
    let mut spans = Vec::new();
    for (index, (key, action)) in keys.iter().enumerate() {
        if index > 0 {
            spans.push(Span::styled(" \u{b7} ", dim()));
        }
        spans.push(Span::styled(*key, Style::new().fg(Color::Cyan)));
        spans.push(Span::raw(" "));
        spans.push(Span::styled(*action, dim()));
    }
    frame.render_widget(Paragraph::new(Line::from(spans)), area);
}

fn format_elapsed(seconds: u64) -> String {
    match seconds {
        0..60 => format!("{seconds}s"),
        60..3600 => format!("{}m", seconds / 60),
        _ => format!("{}h {}m", seconds / 3600, (seconds % 3600) / 60),
    }
}

// ---- event loop ----------------------------------------------------------

/// Run the dashboard until the user quits.
pub fn run(interval: Option<u64>, provider_filter: Option<String>) -> std::io::Result<()> {
    let mut terminal = ratatui::try_init_with_options(TerminalOptions {
        viewport: Viewport::Fullscreen,
    })?;
    terminal.clear()?;
    let result = event_loop(&mut terminal, interval, provider_filter);
    ratatui::restore();
    result
}

fn event_loop(
    terminal: &mut ratatui::DefaultTerminal,
    interval: Option<u64>,
    provider_filter: Option<String>,
) -> std::io::Result<()> {
    let worker = Worker::spawn(provider_filter);
    let mut app = App::new(interval.unwrap_or(DEFAULT_INTERVAL));
    worker.request();

    loop {
        terminal.draw(|frame| draw(frame, &app))?;

        if let Some(results) = worker.poll() {
            app.apply(results);
        }
        if app.due_for_read() {
            app.reading = true;
            worker.request();
        }

        // The poll timeout is what paces the loop: it wakes for a keystroke
        // immediately, and otherwise once a frame to tick the countdowns.
        if !event::poll(FRAME)? {
            continue;
        }
        let Event::Key(key) = event::read()? else {
            continue;
        };
        if key.kind != KeyEventKind::Press {
            continue;
        }

        app.message = None;
        match key.code {
            KeyCode::Char('q') | KeyCode::Esc => return Ok(()),
            KeyCode::Char('c') if key.modifiers.contains(KeyModifiers::CONTROL) => return Ok(()),
            KeyCode::Char('r') => {
                if !app.reading {
                    app.reading = true;
                    worker.request();
                }
            }
            KeyCode::Char('a') => {
                app.show_all = !app.show_all;
                app.clamp_selection();
            }
            KeyCode::Down | KeyCode::Char('j') | KeyCode::Tab => app.move_selection(1),
            KeyCode::Up | KeyCode::Char('k') | KeyCode::BackTab => app.move_selection(-1),
            KeyCode::Char('+') | KeyCode::Char('=') => app.adjust_interval(15),
            KeyCode::Char('-') | KeyCode::Char('_') => app.adjust_interval(-15),
            KeyCode::Char('?') => {
                app.message = Some(
                    "q quit · r refresh now · j/k or arrows select · a toggle errors · +/- change interval"
                        .into(),
                );
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Provider;

    fn healthy(id: &str, percent: f64, reset: &str) -> ProviderUsage {
        let mut usage = ProviderUsage::healthy(
            Provider::Unknown,
            vec![UsageWindow::new("Weekly", percent).reset(reset)],
            "",
        );
        usage.id = id.to_string();
        usage.display_name = id.to_string();
        usage
    }

    #[test]
    fn a_countdown_keeps_ticking_between_reads() {
        let fetched_at = 1_000_000;
        let window = LiveWindow::new(UsageWindow::new("Weekly", 50.0).reset("2h 0m"), fetched_at);

        assert_eq!(window.countdown(fetched_at), "2h 0m");
        assert_eq!(window.countdown(fetched_at + 30 * 60), "1h 30m");
        assert_eq!(window.countdown(fetched_at + 2 * 60 * 60), "Resets now");
    }

    #[test]
    fn an_unreadable_countdown_is_shown_as_the_provider_wrote_it() {
        let window = LiveWindow::new(UsageWindow::new("W", 1.0).reset("Never Resets"), 1_000_000);
        assert_eq!(window.resets_at, None);
        assert_eq!(window.countdown(2_000_000), "Never Resets");
    }

    #[test]
    fn errors_are_hidden_until_asked_for() {
        let mut app = App::new(60);
        app.apply(vec![
            healthy("claude", 20.0, "2h 0m"),
            ProviderUsage::unconfigured(Provider::Groq),
        ]);

        assert_eq!(app.visible().len(), 1);
        app.show_all = true;
        assert_eq!(app.visible().len(), 2);
    }

    #[test]
    fn selection_wraps_around_the_visible_list() {
        let mut app = App::new(60);
        app.apply(vec![
            healthy("a", 1.0, "1h 0m"),
            healthy("b", 2.0, "1h 0m"),
            healthy("c", 3.0, "1h 0m"),
        ]);

        app.move_selection(1);
        assert_eq!(app.selected, 1);
        app.move_selection(-1);
        assert_eq!(app.selected, 0);
        // Off the top wraps to the bottom rather than sticking.
        app.move_selection(-1);
        assert_eq!(app.selected, 2);
        app.move_selection(1);
        assert_eq!(app.selected, 0);
    }

    #[test]
    fn selection_survives_the_list_shrinking() {
        let mut app = App::new(60);
        app.show_all = true;
        app.apply(vec![
            healthy("a", 1.0, "1h 0m"),
            ProviderUsage::unconfigured(Provider::Groq),
        ]);
        app.selected = 1;

        app.show_all = false;
        app.clamp_selection();
        assert_eq!(app.selected, 0);
        assert!(app.selected_provider().is_some());
    }

    #[test]
    fn selection_on_an_empty_list_is_harmless() {
        let mut app = App::new(60);
        app.move_selection(1);
        assert_eq!(app.selected, 0);
        assert!(app.selected_provider().is_none());
    }

    #[test]
    fn history_grows_with_each_healthy_reading_and_stays_bounded() {
        let mut app = App::new(60);
        for step in 0..(HISTORY + 10) {
            app.apply(vec![healthy("claude", step as f64 % 100.0, "1h 0m")]);
        }

        assert_eq!(app.history["claude"].len(), HISTORY);
    }

    #[test]
    fn a_failed_reading_does_not_draw_a_cliff_into_the_trend() {
        let mut app = App::new(60);
        app.apply(vec![healthy("claude", 40.0, "1h 0m")]);
        app.apply(vec![ProviderUsage::degraded(Provider::Claude, "boom")]);

        assert_eq!(app.history.get("claude").map(Vec::len), Some(1));
    }

    #[test]
    fn the_interval_stays_within_its_bounds() {
        let mut app = App::new(60);
        for _ in 0..100 {
            app.adjust_interval(-15);
        }
        assert_eq!(app.interval.as_secs(), MIN_INTERVAL);

        for _ in 0..1000 {
            app.adjust_interval(15);
        }
        assert_eq!(app.interval.as_secs(), MAX_INTERVAL);
    }

    #[test]
    fn an_out_of_range_starting_interval_is_clamped() {
        assert_eq!(App::new(0).interval.as_secs(), MIN_INTERVAL);
        assert_eq!(App::new(99_999).interval.as_secs(), MAX_INTERVAL);
    }

    #[test]
    fn the_first_read_is_due_immediately_and_not_repeated_while_running() {
        let mut app = App::new(60);
        app.reading = false;
        assert!(app.due_for_read());

        app.apply(vec![healthy("a", 1.0, "1h 0m")]);
        assert!(!app.due_for_read());

        app.reading = true;
        assert!(
            !app.due_for_read(),
            "a read in flight must not queue another"
        );
    }

    #[test]
    fn the_spark_uses_a_fixed_scale_so_small_drifts_stay_small() {
        assert_eq!(sparkline(&[0.0, 100.0], 8), "\u{2581}\u{2588}");
        // A drift of one point must not paint as a full-height swing.
        assert_eq!(sparkline(&[40.0, 41.0], 8), "\u{2584}\u{2584}");
        assert_eq!(sparkline(&[50.0], 0), "");
    }

    #[test]
    fn the_spark_keeps_the_most_recent_readings_when_space_runs_out() {
        let points: Vec<f64> = (0..=10).map(|n| n as f64 * 10.0).collect();
        let spark = sparkline(&points, 3);

        // The last three readings are 80, 90, 100 — the oldest are dropped,
        // not the newest.
        assert_eq!(spark.chars().count(), 3);
        assert_eq!(spark.chars().next_back(), Some('\u{2588}'));
        assert_eq!(spark, sparkline(&[80.0, 90.0, 100.0], 3));
    }

    #[test]
    fn severity_matches_the_thresholds_the_cli_uses() {
        assert_eq!(severity(10.0), Color::Green);
        assert_eq!(severity(80.0), Color::Yellow);
        assert_eq!(severity(95.0), Color::Red);
    }

    #[test]
    fn elapsed_time_is_readable_at_every_scale() {
        assert_eq!(format_elapsed(5), "5s");
        assert_eq!(format_elapsed(90), "1m");
        assert_eq!(format_elapsed(3 * 3600 + 25 * 60), "3h 25m");
    }

    /// Render one frame and read the cells back as text, so the layout is
    /// exercised for real rather than only its inputs.
    fn render(app: &App, width: u16, height: u16) -> String {
        let mut terminal =
            ratatui::Terminal::new(ratatui::backend::TestBackend::new(width, height)).unwrap();
        terminal.draw(|frame| draw(frame, app)).unwrap();
        terminal
            .backend()
            .buffer()
            .content()
            .iter()
            .map(|cell| cell.symbol())
            .collect()
    }

    #[test]
    fn a_frame_shows_the_provider_list_and_the_selected_detail() {
        let mut app = App::new(60);
        let mut opencode = ProviderUsage::healthy(
            Provider::OpenCode,
            vec![
                UsageWindow::new("Rolling", 6.0).reset("1h 35m"),
                UsageWindow::new("Weekly", 100.0).reset("2d 4h"),
            ],
            "OpenCode Go subscription",
        );
        opencode.id = "opencode".into();
        app.apply(vec![healthy("claude", 55.0, "1h 3m"), opencode]);

        let screen = render(&app, 100, 20);

        assert!(screen.contains("limits v"), "{screen}");
        assert!(screen.contains("Providers"), "{screen}");
        assert!(screen.contains("claude"), "{screen}");
        assert!(screen.contains("OpenCode Go"), "{screen}");
        // The detail pane follows the selection, which starts at the top.
        assert!(screen.contains("Weekly"), "{screen}");
        assert!(screen.contains("q quit"), "{screen}");
    }

    #[test]
    fn selecting_a_provider_switches_the_detail_pane() {
        let mut app = App::new(60);
        let mut opencode = ProviderUsage::healthy(
            Provider::OpenCode,
            vec![UsageWindow::new("Rolling", 6.0).reset("1h 35m")],
            "OpenCode Go subscription",
        );
        opencode.id = "opencode".into();
        app.apply(vec![healthy("claude", 55.0, "1h 3m"), opencode]);

        assert!(!render(&app, 100, 20).contains("OpenCode Go subscription"));
        app.move_selection(1);
        assert!(render(&app, 100, 20).contains("OpenCode Go subscription"));
    }

    #[test]
    fn an_unconfigured_provider_shows_its_hint_instead_of_bars() {
        let mut app = App::new(60);
        app.show_all = true;
        app.apply(vec![ProviderUsage::unconfigured(Provider::OpenCode)]);

        let screen = render(&app, 100, 20);
        assert!(screen.contains("OPENCODE_GO_API_KEY"), "{screen}");
    }

    #[test]
    fn a_frame_renders_without_panicking_at_awkward_sizes() {
        let mut app = App::new(60);
        app.apply(vec![
            healthy("claude", 55.0, "1h 3m"),
            ProviderUsage::degraded(Provider::Grok, "boom"),
        ]);
        app.show_all = true;

        // A pane too short for its content must clip, not panic.
        for (width, height) in [(20, 3), (40, 5), (200, 60), (12, 2)] {
            let _ = render(&app, width, height);
        }
    }

    #[test]
    fn an_empty_dashboard_says_what_to_do_about_it() {
        let mut app = App::new(60);
        app.apply(vec![]);

        let screen = render(&app, 80, 12);
        assert!(screen.contains("No providers enabled"), "{screen}");
    }
}
