package main

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"

	"github.com/euxaristia/limits/pkg/config"
	"github.com/euxaristia/limits/pkg/fetchers"
	"github.com/euxaristia/limits/pkg/models"
	"github.com/euxaristia/limits/pkg/terminal"
)

const Version = "1.1.0"

func printHelp() {
	fmt.Println(terminal.Bold("Limits CLI — Cross-platform AI Quota & Limit Monitor"))
	fmt.Println(terminal.Dim("Monitor OpenAI, Claude, Gemini, Antigravity, Cursor, DeepSeek, OpenRouter & more"))
	fmt.Println()
	fmt.Println(terminal.Bold("USAGE:"))
	fmt.Println("  limits [command] [options]")
	fmt.Println()
	fmt.Println(terminal.Bold("COMMANDS:"))
	fmt.Println("  status (default)        Fetch and display current usage for all active providers")
	fmt.Println("  providers               List all available providers and their enabled state")
	fmt.Println("  config path             Display active config file path")
	fmt.Println("  config show             Display active configuration JSON")
	fmt.Println("  config enable <id>      Enable a provider by ID")
	fmt.Println("  config disable <id>     Disable a provider by ID")
	fmt.Println("  config set-key <id> <k> Set API key for a provider")
	fmt.Println("  help, -h, --help        Show this help message")
	fmt.Println("  version, -v, --version  Show CLI version")
	fmt.Println()
	fmt.Println(terminal.Bold("OPTIONS:"))
	fmt.Println("  --json                  Output raw JSON format (useful for scripts & status bars)")
	fmt.Println("  -p, --provider <id>     Filter status to a single provider (e.g. -p claude)")
	fmt.Println("  --no-color              Disable colored terminal output")
}

func renderUsage(maxLabelWidth int, u models.ProviderUsage) {
	statusBadge := terminal.Green("[OK]")
	if u.Status == "unconfigured" {
		statusBadge = terminal.Dim("[UNCONFIGURED]")
	} else if u.HasError {
		statusBadge = terminal.Red("[ERROR]")
	} else if u.Status == "degraded" {
		statusBadge = terminal.Yellow("[DEGRADED]")
	}

	fmt.Printf("%s %s\n", terminal.Bold(u.DisplayName), statusBadge)

	if u.HasError {
		if u.Status == "unconfigured" {
			fmt.Printf("  %s\n", terminal.Dim(u.ErrorMessage))
		} else {
			fmt.Printf("  %s\n", terminal.Red(fmt.Sprintf("Error: %s", u.ErrorMessage)))
		}
	} else {
		for _, w := range u.Windows {
			bar := terminal.RenderProgressBar(w.UsedPercent, 20)
			pctText := fmt.Sprintf("%5.1f%%", w.UsedPercent)
			if w.PercentTextOverride != nil {
				pctText = *w.PercentTextOverride
			}

			resetText := ""
			if strings.TrimSpace(w.ResetCountdown) != "" {
				resetText = fmt.Sprintf(" (%s)", w.ResetCountdown)
			}

			paddedLabel := fmt.Sprintf("%-*s", maxLabelWidth, w.Label)
			fmt.Printf("  %s %s %s%s\n", paddedLabel, bar, pctText, terminal.Dim(resetText))
		}

		if strings.TrimSpace(u.Footer) != "" {
			fmt.Printf("  %s\n", terminal.Dim(u.Footer))
		}
	}

	fmt.Println()
}

// displayPriority puts these providers first, in this order; everything else
// keeps its existing relative position from the config file.
var displayPriority = []string{"claude", "grok", "antigravity", "codex"}

func getPriorityRank(id string) (int, bool) {
	for i, p := range displayPriority {
		if strings.EqualFold(p, id) {
			return i, true
		}
	}
	return 0, false
}

func sortProvidersForDisplay(configs []models.ProviderConfig) {
	sort.SliceStable(configs, func(i, j int) bool {
		ri, iOk := getPriorityRank(configs[i].ID)
		rj, jOk := getPriorityRank(configs[j].ID)
		if iOk && jOk {
			return ri < rj
		}
		return iOk && !jOk
	})
}

func isExhausted(u models.ProviderUsage) bool {
	if len(u.Windows) == 0 {
		return false
	}
	for _, w := range u.Windows {
		if w.UsedPercent < 100.0 {
			return false
		}
	}
	return true
}

func resetCountdownSeconds(countdown string) (int, bool) {
	countdown = strings.TrimSpace(countdown)
	if strings.EqualFold(countdown, "Resets now") {
		return 0, true
	}
	total := 0
	fields := strings.Fields(countdown)
	if len(fields) == 0 {
		return 0, false
	}
	for _, field := range fields {
		if len(field) < 2 {
			return 0, false
		}
		value, err := strconv.Atoi(field[:len(field)-1])
		if err != nil || value < 0 {
			return 0, false
		}
		switch field[len(field)-1] {
		case 'd':
			total += value * 24 * 60 * 60
		case 'h':
			total += value * 60 * 60
		case 'm':
			total += value * 60
		case 's':
			total += value
		default:
			return 0, false
		}
	}
	return total, true
}

func earliestResetSeconds(u models.ProviderUsage) (int, bool) {
	earliest := 0
	found := false
	for _, window := range u.Windows {
		seconds, ok := resetCountdownSeconds(window.ResetCountdown)
		if ok && (!found || seconds < earliest) {
			earliest = seconds
			found = true
		}
	}
	return earliest, found
}

func sortResultsByUsage(results []models.ProviderUsage) {
	sort.SliceStable(results, func(i, j int) bool {
		iExhausted := isExhausted(results[i])
		jExhausted := isExhausted(results[j])
		if iExhausted != jExhausted {
			return !iExhausted
		}
		if iExhausted {
			iReset, iHasReset := earliestResetSeconds(results[i])
			jReset, jHasReset := earliestResetSeconds(results[j])
			if iHasReset != jHasReset {
				return iHasReset
			}
			if iHasReset && iReset != jReset {
				return iReset < jReset
			}
		}

		ri, iOk := getPriorityRank(results[i].ID)
		rj, jOk := getPriorityRank(results[j].ID)
		if iOk && jOk {
			return ri < rj
		}
		return iOk && !jOk
	})
}

func fetchAndDisplayStatus(jsonOutput bool, targetProvider string) int {
	cfg := config.Load()

	var activeConfigs []models.ProviderConfig
	for _, p := range cfg.Providers {
		if p.IsEnabled() {
			if targetProvider == "" || strings.EqualFold(p.ID, targetProvider) {
				activeConfigs = append(activeConfigs, p)
			}
		}
	}
	sortProvidersForDisplay(activeConfigs)

	if len(activeConfigs) == 0 {
		if jsonOutput {
			fmt.Println("[]")
		} else {
			fmt.Println(terminal.Yellow("No active providers enabled in configuration."))
			fmt.Println("Run 'limits providers' to view available providers or 'limits config enable <id>'.")
		}
		return 0
	}

	tasks := fetchers.FetchAllConcurrent(activeConfigs)

	var results []models.ProviderUsage
	for _, u := range tasks {
		isErratic := u.Status == "unconfigured" || u.Status == "degraded" || u.Status == "error" || u.HasError
		if !isErratic {
			results = append(results, u)
		}
	}
	sortResultsByUsage(results)

	if jsonOutput {
		if results == nil {
			results = []models.ProviderUsage{}
		}
		data, err := json.MarshalIndent(results, "", "  ")
		if err != nil {
			fmt.Println("[]")
		} else {
			fmt.Println(string(data))
		}
		return 0
	}

	if len(results) == 0 {
		fmt.Println(terminal.Yellow("No configured providers active."))
		fmt.Println("Run 'limits providers' to view available providers or set an API key with 'limits config set-key <id> <key>'.")
		return 0
	}

	maxLabelWidth := 24
	for _, u := range results {
		for _, w := range u.Windows {
			if len(w.Label) > maxLabelWidth {
				maxLabelWidth = len(w.Label)
			}
		}
	}

	fmt.Println(terminal.Bold("── AI Limits & Quotas ────────────────────────────────"))
	fmt.Println()
	for _, r := range results {
		renderUsage(maxLabelWidth, r)
	}

	return 0
}

func handleConfigCommand(args []string) int {
	cfg := config.Load()
	if len(args) == 0 {
		fmt.Println(terminal.Red("Invalid config subcommand."))
		fmt.Println("Usage: limits config [path | show | enable <id> | disable <id> | set-key <id> <key>]")
		return 1
	}

	switch args[0] {
	case "path":
		fmt.Println(config.GetDefaultConfigPath())
		return 0
	case "show":
		data, _ := json.MarshalIndent(cfg, "", "  ")
		fmt.Println(string(data))
		return 0
	case "enable":
		if len(args) < 2 {
			fmt.Println(terminal.Red("Missing provider ID."))
			return 1
		}
		id := args[1]
		trueVal := true
		for i, p := range cfg.Providers {
			if strings.EqualFold(p.ID, id) {
				cfg.Providers[i].Enabled = &trueVal
			}
		}
		_ = config.Save(cfg)
		fmt.Println(terminal.Green(fmt.Sprintf("Enabled provider '%s'.", id)))
		return 0
	case "disable":
		if len(args) < 2 {
			fmt.Println(terminal.Red("Missing provider ID."))
			return 1
		}
		id := args[1]
		falseVal := false
		for i, p := range cfg.Providers {
			if strings.EqualFold(p.ID, id) {
				cfg.Providers[i].Enabled = &falseVal
			}
		}
		_ = config.Save(cfg)
		fmt.Println(terminal.Yellow(fmt.Sprintf("Disabled provider '%s'.", id)))
		return 0
	case "set-key":
		if len(args) < 3 {
			fmt.Println(terminal.Red("Usage: limits config set-key <id> <key>"))
			return 1
		}
		id := args[1]
		key := args[2]
		for i, p := range cfg.Providers {
			if strings.EqualFold(p.ID, id) {
				cfg.Providers[i].APIKey = key
			}
		}
		_ = config.Save(cfg)
		fmt.Println(terminal.Green(fmt.Sprintf("Updated API key for provider '%s'.", id)))
		return 0
	default:
		fmt.Println(terminal.Red("Invalid config subcommand."))
		fmt.Println("Usage: limits config [path | show | enable <id> | disable <id> | set-key <id> <key>]")
		return 1
	}
}

func listProviders() {
	cfg := config.Load()

	sort.SliceStable(cfg.Providers, func(i, j int) bool {
		iEnabled := cfg.Providers[i].IsEnabled()
		jEnabled := cfg.Providers[j].IsEnabled()
		if iEnabled != jEnabled {
			return iEnabled
		}
		return strings.ToLower(cfg.Providers[i].ID) < strings.ToLower(cfg.Providers[j].ID)
	})

	fmt.Println(terminal.Bold("Available Providers:"))
	fmt.Println()
	for _, p := range cfg.Providers {
		providerEnum := models.ProviderFromString(p.ID)
		displayName := models.GetDisplayName(providerEnum)
		if displayName == "Unknown" {
			displayName = strings.Title(p.ID)
		}
		isEnabled := p.IsEnabled()

		statusStr := terminal.Dim("[DISABLED]")
		if isEnabled {
			statusStr = terminal.Green("[ENABLED] ")
		}

		keyStr := terminal.Dim("(No API Key)")
		if strings.TrimSpace(p.APIKey) != "" {
			keyStr = terminal.Cyan("(API Key Set)")
		}

		fmt.Printf("  %-18s %s %-18s %s\n", p.ID, statusStr, displayName, keyStr)
	}
	fmt.Println()
}

func main() {
	args := os.Args[1:]

	jsonMode := false
	filterProvider := ""
	cleanArgs := make([]string, 0, len(args))

	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--json" {
			jsonMode = true
		} else if a == "--no-color" {
			terminal.DisableColor = true
		} else if (a == "-p" || a == "--provider") && i+1 < len(args) {
			filterProvider = args[i+1]
			i++
		} else {
			cleanArgs = append(cleanArgs, a)
		}
	}

	cmd := "status"
	if len(cleanArgs) > 0 {
		cmd = cleanArgs[0]
	}

	switch cmd {
	case "status":
		os.Exit(fetchAndDisplayStatus(jsonMode, filterProvider))
	case "providers", "list":
		listProviders()
		os.Exit(0)
	case "config":
		os.Exit(handleConfigCommand(cleanArgs[1:]))
	case "-v", "--version", "version":
		fmt.Printf("limits v%s\n", Version)
		os.Exit(0)
	case "-h", "--help", "help":
		printHelp()
		os.Exit(0)
	default:
		if len(cleanArgs) == 0 {
			os.Exit(fetchAndDisplayStatus(jsonMode, filterProvider))
		}
		fmt.Println(terminal.Red("Unknown command."))
		fmt.Println()
		printHelp()
		os.Exit(1)
	}
}
