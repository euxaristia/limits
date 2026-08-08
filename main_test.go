package main

import (
	"strconv"
	"testing"

	"github.com/euxaristia/limits/pkg/models"
)

func TestIsExhausted(t *testing.T) {
	grok := models.ProviderUsage{
		ID: "grok",
		Windows: []models.UsageWindow{
			{Label: "Weekly", UsedPercent: 100.0},
		},
	}
	if !isExhausted(grok) {
		t.Errorf("expected grok (100%% used) to be exhausted")
	}

	multiExhausted := models.ProviderUsage{
		ID: "multi-exhausted",
		Windows: []models.UsageWindow{
			{Label: "Session", UsedPercent: 100.0},
			{Label: "Weekly", UsedPercent: 100.0},
		},
	}
	if !isExhausted(multiExhausted) {
		t.Errorf("expected multi-window exhausted provider to be exhausted")
	}

	antigravity := models.ProviderUsage{
		ID: "antigravity",
		Windows: []models.UsageWindow{
			{Label: "Session Gemini", UsedPercent: 2.0},
			{Label: "Weekly Gemini", UsedPercent: 24.0},
			{Label: "Weekly Claude & GPT", UsedPercent: 100.0},
		},
	}
	if isExhausted(antigravity) {
		t.Errorf("expected antigravity (has 2%% and 24%% used windows) not to be exhausted")
	}

	empty := models.ProviderUsage{ID: "empty"}
	if isExhausted(empty) {
		t.Errorf("expected empty windows not to be exhausted")
	}
}

func TestSortResultsByUsage_DemotesExhaustedProviders(t *testing.T) {
	// Setup matching user's scenario:
	// Claude: 28%/49% used -> Usable
	// Grok: 100% used -> Exhausted
	// Antigravity: 2%/24%/100% used -> Usable (has Gemini quota remaining)
	// Codex: 100% used -> Exhausted
	// Copilot: 5.9%/0.0% used -> Usable
	grok := models.ProviderUsage{
		ID: "GrOk",
		Windows: []models.UsageWindow{
			{Label: "Weekly", UsedPercent: 100.0, ResetCountdown: "3d 2h"},
		},
	}
	codex := models.ProviderUsage{
		ID: "CoDeX",
		Windows: []models.UsageWindow{
			{Label: "Primary", UsedPercent: 100.0, ResetCountdown: "1h 30m"},
		},
	}
	antigravity := models.ProviderUsage{
		ID: "AnTiGrAvItY",
		Windows: []models.UsageWindow{
			{Label: "Session Gemini", UsedPercent: 2.0},
			{Label: "Weekly Gemini", UsedPercent: 24.0},
			{Label: "Weekly Claude & GPT", UsedPercent: 100.0},
		},
	}
	claude := models.ProviderUsage{
		ID: "ClAuDe",
		Windows: []models.UsageWindow{
			{Label: "Session", UsedPercent: 28.0},
			{Label: "Weekly", UsedPercent: 49.0},
		},
	}
	copilot := models.ProviderUsage{
		ID: "CoPiLoT",
		Windows: []models.UsageWindow{
			{Label: "Chat", UsedPercent: 5.9},
			{Label: "Completions", UsedPercent: 0.0},
		},
	}

	results := []models.ProviderUsage{claude, grok, antigravity, codex, copilot}
	sortResultsByUsage(results)

	// Usable providers (claude, antigravity, copilot) should come first in priority order,
	// demoting exhausted providers (grok, codex) to the bottom.
	expectedOrder := []string{"ClAuDe", "AnTiGrAvItY", "CoPiLoT", "CoDeX", "GrOk"}
	for i, want := range expectedOrder {
		if results[i].ID != want {
			t.Errorf("at index %d: expected %s, got %s", i, want, results[i].ID)
		}
	}
}

func TestSortResultsByUsage_ExhaustedUnknownResetSortsLast(t *testing.T) {
	results := []models.ProviderUsage{
		{ID: "grok", Windows: []models.UsageWindow{{UsedPercent: 100, ResetCountdown: "Unknown"}}},
		{ID: "codex", Windows: []models.UsageWindow{{UsedPercent: 100, ResetCountdown: "Resets now"}}},
	}
	sortResultsByUsage(results)
	if results[0].ID != "codex" {
		t.Errorf("expected known sooner reset first, got %s", results[0].ID)
	}
}

func TestResetCountdownSecondsRejectsOverflow(t *testing.T) {
	maxInt := int(^uint(0) >> 1)
	if seconds, ok := resetCountdownSeconds(strconv.Itoa(maxInt) + "s"); !ok || seconds != maxInt {
		t.Fatalf("expected maximum int seconds to parse, got %d, %t", seconds, ok)
	}
	for _, unit := range []string{"d", "h", "m"} {
		if _, ok := resetCountdownSeconds(strconv.Itoa(maxInt) + unit); ok {
			t.Errorf("expected %s conversion overflow to be rejected", unit)
		}
	}
	if _, ok := resetCountdownSeconds(strconv.Itoa(maxInt) + "s 1s"); ok {
		t.Error("expected cumulative overflow to be rejected")
	}
}
