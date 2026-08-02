package parsers

import (
	"encoding/json"
	"fmt"
	"math"
	"time"
)

// CodexUsageWindow is one rolling ChatGPT Codex allowance window.
type CodexUsageWindow struct {
	Label          string
	UsedPercent    float64
	WindowSeconds  int
	ResetCountdown string
}

// CodexUsage is the portion of Codex's /wham/usage response relevant to limits.
type CodexUsage struct {
	PlanType string
	Windows  []CodexUsageWindow
}

// ParseCodexUsage parses the public response shape used by the Codex CLI.
func ParseCodexUsage(data []byte, now time.Time) (CodexUsage, error) {
	var payload struct {
		PlanType  string `json:"plan_type"`
		RateLimit struct {
			PrimaryWindow   *codexWindow `json:"primary_window"`
			SecondaryWindow *codexWindow `json:"secondary_window"`
		} `json:"rate_limit"`
	}
	if err := json.Unmarshal(data, &payload); err != nil {
		return CodexUsage{}, err
	}

	windows := make([]CodexUsageWindow, 0, 2)
	if window := payload.RateLimit.PrimaryWindow; window != nil {
		windows = append(windows, window.toUsageWindow("Primary", now))
	}
	if window := payload.RateLimit.SecondaryWindow; window != nil {
		windows = append(windows, window.toUsageWindow("Secondary", now))
	}
	if len(windows) == 0 {
		return CodexUsage{}, fmt.Errorf("no Codex rate-limit windows in response")
	}
	return CodexUsage{PlanType: payload.PlanType, Windows: windows}, nil
}

type codexWindow struct {
	UsedPercent        float64 `json:"used_percent"`
	LimitWindowSeconds int     `json:"limit_window_seconds"`
	ResetAfterSeconds  int     `json:"reset_after_seconds"`
	ResetAt            int64   `json:"reset_at"`
}

func (w codexWindow) toUsageWindow(label string, now time.Time) CodexUsageWindow {
	reset := "Unknown"
	if w.ResetAt > 0 {
		reset = countdownTo(time.Unix(w.ResetAt, 0), now)
	} else if w.ResetAfterSeconds >= 0 {
		reset = countdownTo(now.Add(time.Duration(w.ResetAfterSeconds)*time.Second), now)
	}
	return CodexUsageWindow{Label: label, UsedPercent: math.Max(0, math.Min(100, w.UsedPercent)), WindowSeconds: w.LimitWindowSeconds, ResetCountdown: reset}
}

func countdownTo(resetAt, now time.Time) string {
	seconds := int(resetAt.Sub(now).Seconds())
	if seconds <= 0 {
		return "Resets now"
	}
	hours := seconds / 3600
	minutes := (seconds % 3600) / 60
	if hours >= 24 {
		return fmt.Sprintf("%dd %dh", hours/24, hours%24)
	}
	if hours > 0 {
		return fmt.Sprintf("%dh %dm", hours, minutes)
	}
	return fmt.Sprintf("%dm", minutes)
}
