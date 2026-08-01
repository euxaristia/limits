package parsers

import (
	"encoding/json"
	"fmt"
	"math"
	"time"
)

type ClaudeBucket struct {
	HasData        bool
	Used           float64
	ResetCountdown string
}

type ClaudeParseResult struct {
	PrimaryUsed  float64
	PrimaryReset string
	PrimaryLabel string
	CostInfo     string
	Session      *ClaudeBucket
	Weekly       *ClaudeBucket
}

func readClaudeBucket(parent map[string]interface{}, name string, defaultWindow string, defaultDuration time.Duration) ClaudeBucket {
	val, ok := parent[name]
	if !ok || val == nil {
		return ClaudeBucket{HasData: false, Used: 0, ResetCountdown: "Never Resets"}
	}
	bMap, ok := val.(map[string]interface{})
	if !ok {
		return ClaudeBucket{HasData: false, Used: 0, ResetCountdown: "Never Resets"}
	}

	var used float64
	if util, ok := bMap["utilization"].(float64); ok {
		used = util
	}

	countdown := "Never Resets"
	if resets, ok := bMap["resets_at"].(string); ok && resets != "" {
		parsed := FormatCountdown(resets)
		if parsed != "Unknown" {
			countdown = parsed
		} else {
			fallbackTime := time.Now().UTC().Add(defaultDuration).Format(time.RFC3339)
			countdown = fmt.Sprintf("in %s", FormatCountdown(fallbackTime))
		}
	} else {
		fallbackTime := time.Now().UTC().Add(defaultDuration).Format(time.RFC3339)
		countdown = fmt.Sprintf("in %s", FormatCountdown(fallbackTime))
	}

	return ClaudeBucket{
		HasData:        true,
		Used:           used,
		ResetCountdown: countdown,
	}
}

func ParseClaudeUsage(data []byte) ClaudeParseResult {
	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return ClaudeParseResult{PrimaryUsed: 0, PrimaryReset: "Never Resets", PrimaryLabel: "Claude Plan", CostInfo: "Claude Plan"}
	}

	session := readClaudeBucket(root, "five_hour", "5h", 5*time.Hour)
	weekly := readClaudeBucket(root, "seven_day", "7d", 7*24*time.Hour)

	sessionPct := math.Round(session.Used*10) / 10
	weeklyPct := math.Round(weekly.Used*10) / 10

	var primaryPct float64
	var primaryReset string
	var primaryLabel string

	switch {
	case !session.HasData && !weekly.HasData:
		primaryPct, primaryReset, primaryLabel = 0.0, "Never Resets", "Claude Plan"
	case session.HasData && !weekly.HasData:
		primaryPct, primaryReset, primaryLabel = session.Used, session.ResetCountdown, "5-hour session quota"
	case !session.HasData && weekly.HasData:
		primaryPct, primaryReset, primaryLabel = weekly.Used, weekly.ResetCountdown, "7-day weekly quota"
	default:
		if weekly.Used >= session.Used {
			primaryPct, primaryReset, primaryLabel = weekly.Used, weekly.ResetCountdown, "7-day weekly quota"
		} else {
			primaryPct, primaryReset, primaryLabel = session.Used, session.ResetCountdown, "5-hour session quota"
		}
	}

	costInfo := primaryLabel
	if session.HasData && weekly.HasData {
		costInfo = fmt.Sprintf("Session: %g%% · 7-day: %g%%", sessionPct, weeklyPct)
	}

	res := ClaudeParseResult{
		PrimaryUsed:  math.Max(0.0, math.Min(100.0, primaryPct)),
		PrimaryReset: primaryReset,
		PrimaryLabel: primaryLabel,
		CostInfo:     costInfo,
	}
	if session.HasData {
		res.Session = &session
	}
	if weekly.HasData {
		res.Weekly = &weekly
	}
	return res
}
