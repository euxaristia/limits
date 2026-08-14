//! The command line: argument handling, terminal styling, and the one-shot
//! status report.

use crate::model::{LimitsConfig, Provider, ProviderConfig, ProviderUsage, Status, UsageWindow};
use crate::{Limits, VERSION, config};
use std::io::{IsTerminal, Write};
use std::sync::atomic::{AtomicBool, Ordering};

// ---- styling -------------------------------------------------------------

static COLOR_DISABLED: AtomicBool = AtomicBool::new(false);

pub fn disable_color() {
    COLOR_DISABLED.store(true, Ordering::Relaxed);
}

/// Whether to emit ANSI escapes.
///
/// `FORCE_COLOR=1` wins so colour survives a pipe into `less -R`; `NO_COLOR`
/// wins over the terminal check, per the convention; and output that is not a
/// terminal is left plain so a status-bar script gets clean text.
pub fn use_color() -> bool {
    if COLOR_DISABLED.load(Ordering::Relaxed) {
        return false;
    }
    if std::env::var("FORCE_COLOR").is_ok_and(|v| v == "1") {
        return true;
    }
    if std::env::var_os("NO_COLOR").is_some_and(|v| !v.is_empty()) {
        return false;
    }
    std::io::stdout().is_terminal()
}

fn paint(code: &str, text: &str) -> String {
    if use_color() {
        format!("\u{1b}[{code}m{text}\u{1b}[0m")
    } else {
        text.to_string()
    }
}

pub fn bold(text: &str) -> String {
    paint("1", text)
}
pub fn dim(text: &str) -> String {
    paint("2", text)
}
pub fn red(text: &str) -> String {
    paint("31", text)
}
pub fn green(text: &str) -> String {
    paint("32", text)
}
pub fn yellow(text: &str) -> String {
    paint("33", text)
}
pub fn cyan(text: &str) -> String {
    paint("36", text)
}

/// Thresholds at which a bar changes colour: comfortable, getting tight, and
/// nearly gone.
pub fn severity_paint(percent: f64, text: &str) -> String {
    if percent > 90.0 {
        red(text)
    } else if percent > 75.0 {
        yellow(text)
    } else {
        green(text)
    }
}

pub fn progress_bar(percent: f64, width: usize) -> String {
    let clamped = percent.clamp(0.0, 100.0);
    let filled = ((clamped / 100.0) * width as f64).round() as usize;
    let filled = filled.min(width);
    let bar = format!(
        "[{}{}]",
        "\u{2588}".repeat(filled),
        "\u{2591}".repeat(width - filled)
    );
    severity_paint(clamped, &bar)
}

// ---- rendering -----------------------------------------------------------

fn status_badge(usage: &ProviderUsage) -> String {
    match usage.status {
        Status::Unconfigured => dim("[UNCONFIGURED]"),
        Status::Degraded => yellow("[DEGRADED]"),
        Status::Healthy if usage.has_error => red("[ERROR]"),
        Status::Healthy => green("[OK]"),
    }
}

/// Width of the label column, wide enough for the longest label present so the
/// bars line up into a single column.
fn label_width(results: &[ProviderUsage]) -> usize {
    results
        .iter()
        .flat_map(|usage| usage.windows.iter())
        .map(|window| window.label.chars().count())
        .max()
        .unwrap_or(0)
        .max(24)
}

fn render_window(out: &mut impl Write, width: usize, window: &UsageWindow) -> std::io::Result<()> {
    let reset = match window.reset_countdown.trim() {
        "" => String::new(),
        text => dim(&format!(" ({text})")),
    };
    writeln!(
        out,
        "  {:<width$} {} {:>7}{reset}",
        window.label,
        progress_bar(window.used_percent, 20),
        window.percent_text(),
    )
}

pub fn render_usage(
    out: &mut impl Write,
    width: usize,
    usage: &ProviderUsage,
) -> std::io::Result<()> {
    writeln!(out, "{} {}", bold(&usage.display_name), status_badge(usage))?;

    if usage.has_error {
        let message = match usage.status {
            Status::Unconfigured => dim(&usage.error_message),
            _ => red(&format!("Error: {}", usage.error_message)),
        };
        writeln!(out, "  {message}")?;
    } else {
        for window in &usage.windows {
            render_window(out, width, window)?;
        }
        if !usage.footer.trim().is_empty() {
            writeln!(out, "  {}", dim(&usage.footer))?;
        }
    }
    writeln!(out)
}

pub fn render_report(out: &mut impl Write, results: &[ProviderUsage]) -> std::io::Result<()> {
    let width = label_width(results);
    writeln!(
        out,
        "{}",
        bold(
            "\u{2500}\u{2500} AI Limits & Quotas \u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}\u{2500}"
        )
    )?;
    writeln!(out)?;
    for usage in results {
        render_usage(out, width, usage)?;
    }
    Ok(())
}

// ---- argument parsing ----------------------------------------------------

#[derive(Debug, PartialEq)]
pub struct Args {
    pub command: Vec<String>,
    pub json: bool,
    pub no_color: bool,
    pub all: bool,
    pub provider: Option<String>,
    pub interval: Option<u64>,
    pub error: Option<String>,
}

/// Split flags from the command words.
///
/// Deliberately hand-rolled rather than pulling in a parser: the grammar is
/// eight flags wide and this keeps the library's dependency surface at two
/// crates for an embedder who wants only the quota reading.
pub fn parse_args(raw: &[String]) -> Args {
    let mut args = Args {
        command: Vec::new(),
        json: false,
        no_color: false,
        all: false,
        provider: None,
        interval: None,
        error: None,
    };

    let mut rest = raw.iter();
    while let Some(argument) = rest.next() {
        match argument.as_str() {
            "--json" => args.json = true,
            "--no-color" => args.no_color = true,
            "-a" | "--all" => args.all = true,
            "-p" | "--provider" => match rest.next() {
                Some(value) => args.provider = Some(value.clone()),
                None => args.error = Some("-p/--provider needs a provider id".into()),
            },
            "-i" | "--interval" => match rest.next().map(|v| v.parse::<u64>()) {
                Some(Ok(seconds)) if seconds > 0 => args.interval = Some(seconds),
                Some(Ok(_)) => args.error = Some("--interval must be at least 1 second".into()),
                Some(Err(_)) => args.error = Some("--interval needs a number of seconds".into()),
                None => args.error = Some("--interval needs a number of seconds".into()),
            },
            other => args.command.push(other.to_string()),
        }
    }
    args
}

// ---- commands ------------------------------------------------------------

pub fn print_help() {
    println!("{}", bold("limits - AI quota and usage monitor"));
    println!(
        "{}",
        dim("Claude, Codex, Antigravity, Grok, Copilot, OpenCode Go, Gemini, and more")
    );
    println!();
    println!("{}", bold("USAGE:"));
    println!("  limits [command] [options]");
    println!();
    println!("{}", bold("COMMANDS:"));
    println!("  status (default)        Fetch and show current usage for all enabled providers");
    println!("  tui, watch              Full-screen live dashboard with continuous updates");
    println!("  providers               List all providers and whether they are enabled");
    println!("  config path             Show the active config file path");
    println!("  config show             Show the active configuration");
    println!("  config enable <id>      Enable a provider");
    println!("  config disable <id>     Disable a provider");
    println!("  config set-key <id> <k> Set a provider's API key");
    println!("  help, -h, --help        Show this message");
    println!("  version, -v, --version  Show the version");
    println!();
    println!("{}", bold("OPTIONS:"));
    println!("  --json                  Emit JSON, for status bars and scripts");
    println!("  -p, --provider <id>     Limit output to one provider");
    println!("  -a, --all               Include providers that reported an error");
    println!("  -i, --interval <secs>   Refresh interval for the dashboard (default 60)");
    println!("  --no-color              Disable coloured output");
}

fn matches_provider(config: &ProviderConfig, wanted: Option<&str>) -> bool {
    wanted.is_none_or(|id| config.id.eq_ignore_ascii_case(id))
}

/// Readings worth showing. Errors are hidden by default because the usual
/// report has a dozen providers of which two are configured, and a wall of
/// "unconfigured" buries the numbers the user asked for. `--all` shows them.
pub fn presentable(results: Vec<ProviderUsage>, all: bool) -> Vec<ProviderUsage> {
    if all {
        return results;
    }
    results
        .into_iter()
        .filter(|usage| usage.status == Status::Healthy && !usage.has_error)
        .collect()
}

fn status_command(args: &Args) -> i32 {
    let limits = Limits::new();
    let requested = args.provider.as_deref();

    if let Some(id) = requested
        && Provider::from(id) == Provider::Unknown
    {
        eprintln!("{}", red(&format!("Unknown provider '{id}'.")));
        eprintln!("Run 'limits providers' to see the available ids.");
        return 1;
    }

    let results = limits.snapshot_filtered(|config| matches_provider(config, requested));
    let shown = presentable(results, args.all || args.json);

    if args.json {
        let json = serde_json::to_string_pretty(&shown).unwrap_or_else(|_| "[]".into());
        println!("{json}");
        return 0;
    }

    if shown.is_empty() {
        println!("{}", yellow("No provider reported usable quota."));
        println!(
            "Run 'limits status --all' to see errors, or 'limits providers' to check what is enabled."
        );
        return 0;
    }

    let mut out = std::io::stdout().lock();
    let _ = render_report(&mut out, &shown);
    0
}

fn providers_command() -> i32 {
    let mut config = config::load();
    config.providers.sort_by(|a, b| {
        b.is_enabled()
            .cmp(&a.is_enabled())
            .then_with(|| a.id.to_lowercase().cmp(&b.id.to_lowercase()))
    });

    println!("{}", bold("Available providers:"));
    println!();
    for provider in &config.providers {
        let state = if provider.is_enabled() {
            green("[ENABLED] ")
        } else {
            dim("[DISABLED]")
        };
        let key = if provider.has_api_key() {
            cyan("(API key set)")
        } else {
            dim("(no API key)")
        };
        println!(
            "  {:<14} {state} {:<16} {key}",
            provider.id,
            provider.provider().display_name()
        );
    }
    println!();
    0
}

fn config_command(words: &[String]) -> i32 {
    let usage =
        "Usage: limits config [path | show | enable <id> | disable <id> | set-key <id> <key>]";
    let Some(subcommand) = words.first().map(String::as_str) else {
        eprintln!("{}", red("Missing config subcommand."));
        eprintln!("{usage}");
        return 1;
    };

    match subcommand {
        "path" => {
            println!("{}", config::config_path().display());
            0
        }
        "show" => {
            let config = config::load();
            println!(
                "{}",
                serde_json::to_string_pretty(&config).unwrap_or_else(|_| "{}".into())
            );
            0
        }
        "enable" | "disable" => {
            let enable = subcommand == "enable";
            let Some(id) = words.get(1) else {
                eprintln!("{}", red("Missing provider id."));
                return 1;
            };
            update_provider(
                id,
                |provider| provider.enabled = Some(enable),
                |id| {
                    if enable {
                        green(&format!("Enabled provider '{id}'."))
                    } else {
                        yellow(&format!("Disabled provider '{id}'."))
                    }
                },
            )
        }
        "set-key" => {
            let (Some(id), Some(key)) = (words.get(1), words.get(2)) else {
                eprintln!("{}", red("Usage: limits config set-key <id> <key>"));
                return 1;
            };
            let key = key.clone();
            update_provider(
                id,
                move |provider| provider.api_key = key.clone(),
                |id| green(&format!("Updated API key for provider '{id}'.")),
            )
        }
        _ => {
            eprintln!(
                "{}",
                red(&format!("Unknown config subcommand '{subcommand}'."))
            );
            eprintln!("{usage}");
            1
        }
    }
}

/// Apply a change to one provider and write the config back.
///
/// An id that matches nothing is an error rather than a silent no-op: the old
/// behaviour was to report success for a typo and change nothing.
fn update_provider(
    id: &str,
    change: impl FnMut(&mut ProviderConfig),
    success: impl Fn(&str) -> String,
) -> i32 {
    let mut change = change;
    let mut config: LimitsConfig = config::load();
    let Some(provider) = config.get_mut(id) else {
        eprintln!("{}", red(&format!("Unknown provider '{id}'.")));
        eprintln!("Run 'limits providers' to see the available ids.");
        return 1;
    };
    change(provider);

    match config::save(&config) {
        Ok(()) => {
            println!("{}", success(id));
            0
        }
        Err(e) => {
            eprintln!("{}", red(&format!("Could not write config: {e}")));
            1
        }
    }
}

/// Run the CLI. Returns the process exit code.
pub fn run(raw: &[String]) -> i32 {
    let args = parse_args(raw);
    if args.no_color {
        disable_color();
    }
    if let Some(error) = &args.error {
        eprintln!("{}", red(error));
        return 1;
    }

    let command = args.command.first().map(String::as_str).unwrap_or("status");
    match command {
        "status" => status_command(&args),
        "providers" | "list" => providers_command(),
        "config" => config_command(&args.command[1..]),
        "tui" | "watch" | "top" => run_dashboard(&args),
        "version" | "-v" | "--version" => {
            println!("limits v{VERSION}");
            0
        }
        "help" | "-h" | "--help" => {
            print_help();
            0
        }
        unknown => {
            eprintln!("{}", red(&format!("Unknown command '{unknown}'.")));
            eprintln!();
            print_help();
            1
        }
    }
}

#[cfg(feature = "tui")]
fn run_dashboard(args: &Args) -> i32 {
    match crate::tui::run(args.interval, args.provider.clone()) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("{}", red(&format!("Dashboard failed: {e}")));
            1
        }
    }
}

#[cfg(not(feature = "tui"))]
fn run_dashboard(_args: &Args) -> i32 {
    eprintln!(
        "{}",
        red("This build has no dashboard. Rebuild with the 'tui' feature.")
    );
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Provider;

    fn args(words: &[&str]) -> Args {
        parse_args(&words.iter().map(|w| w.to_string()).collect::<Vec<_>>())
    }

    #[test]
    fn flags_are_separated_from_command_words() {
        let parsed = args(&["status", "--json", "-p", "claude", "--no-color"]);

        assert_eq!(parsed.command, ["status"]);
        assert!(parsed.json);
        assert!(parsed.no_color);
        assert_eq!(parsed.provider.as_deref(), Some("claude"));
        assert_eq!(parsed.error, None);
    }

    #[test]
    fn config_words_survive_parsing_in_order() {
        let parsed = args(&["config", "set-key", "opencode", "sk-test"]);
        assert_eq!(parsed.command, ["config", "set-key", "opencode", "sk-test"]);
    }

    #[test]
    fn a_flag_missing_its_value_is_an_error_not_a_silent_default() {
        assert!(args(&["-p"]).error.is_some());
        assert!(args(&["--interval"]).error.is_some());
        assert!(args(&["--interval", "abc"]).error.is_some());
        assert!(args(&["--interval", "0"]).error.is_some());
    }

    #[test]
    fn an_interval_is_read_as_seconds() {
        assert_eq!(args(&["tui", "-i", "15"]).interval, Some(15));
    }

    #[test]
    fn no_arguments_means_status() {
        let parsed = args(&[]);
        assert!(parsed.command.is_empty());
        assert_eq!(
            parsed
                .command
                .first()
                .map(String::as_str)
                .unwrap_or("status"),
            "status"
        );
    }

    #[test]
    fn the_bar_fills_in_proportion() {
        crate::cli::disable_color();
        assert_eq!(progress_bar(0.0, 4), "[░░░░]");
        assert_eq!(progress_bar(50.0, 4), "[██░░]");
        assert_eq!(progress_bar(100.0, 4), "[████]");
        // Out-of-range input must not produce a bar longer than the column.
        assert_eq!(progress_bar(140.0, 4), "[████]");
        assert_eq!(progress_bar(-5.0, 4), "[░░░░]");
    }

    #[test]
    fn errors_are_hidden_unless_asked_for() {
        let results = vec![
            ProviderUsage::healthy(Provider::Claude, vec![UsageWindow::new("S", 1.0)], ""),
            ProviderUsage::unconfigured(Provider::Groq),
            ProviderUsage::degraded(Provider::Grok, "boom"),
        ];

        assert_eq!(presentable(results.clone(), false).len(), 1);
        assert_eq!(presentable(results, true).len(), 3);
    }

    #[test]
    fn a_report_renders_every_window_and_the_footer() {
        crate::cli::disable_color();
        let usage = ProviderUsage::healthy(
            Provider::OpenCode,
            vec![
                UsageWindow::new("Rolling", 6.0).reset("2h 0m"),
                UsageWindow::new("Weekly", 100.0).reset("2d 3h"),
            ],
            "OpenCode Go subscription",
        );

        let mut out = Vec::new();
        render_report(&mut out, &[usage]).unwrap();
        let text = String::from_utf8(out).unwrap();

        assert!(text.contains("OpenCode Go [OK]"), "{text}");
        assert!(text.contains("Rolling"), "{text}");
        assert!(text.contains("6.0%"), "{text}");
        assert!(text.contains("(2h 0m)"), "{text}");
        assert!(text.contains("OpenCode Go subscription"), "{text}");
    }

    #[test]
    fn an_unconfigured_provider_shows_its_setup_hint_not_a_bar() {
        crate::cli::disable_color();
        let mut out = Vec::new();
        render_usage(
            &mut out,
            24,
            &ProviderUsage::unconfigured(Provider::OpenCode),
        )
        .unwrap();
        let text = String::from_utf8(out).unwrap();

        assert!(text.contains("[UNCONFIGURED]"), "{text}");
        assert!(text.contains("OPENCODE_GO_API_KEY"), "{text}");
        assert!(!text.contains('█'), "{text}");
    }

    #[test]
    fn a_percent_override_replaces_the_number() {
        crate::cli::disable_color();
        let usage = ProviderUsage::healthy(
            Provider::Copilot,
            vec![UsageWindow::new("Completions", 0.0).text("Unlimited")],
            "",
        );

        let mut out = Vec::new();
        render_usage(&mut out, 24, &usage).unwrap();
        let text = String::from_utf8(out).unwrap();

        assert!(text.contains("Unlimited"), "{text}");
        assert!(!text.contains("0.0%"), "{text}");
    }

    #[test]
    fn the_label_column_widens_to_the_longest_label() {
        let usage = ProviderUsage::healthy(
            Provider::Antigravity,
            vec![UsageWindow::new(
                "Weekly Claude & GPT (3 models) and then some",
                1.0,
            )],
            "",
        );
        assert_eq!(label_width(&[usage]), 44);
        assert_eq!(label_width(&[]), 24);
    }

    #[test]
    fn color_is_suppressed_when_asked() {
        disable_color();
        assert!(!use_color());
        assert_eq!(bold("x"), "x");
        assert_eq!(red("x"), "x");
    }
}
