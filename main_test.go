package main

import (
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
		ID: "grok",
		Windows: []models.UsageWindow{
			{Label: "Weekly", UsedPercent: 100.0},
		},
	}
	codex := models.ProviderUsage{
		ID: "codex",
		Windows: []models.UsageWindow{
			{Label: "Primary", UsedPercent: 100.0},
		},
	}
	antigravity := models.ProviderUsage{
		ID: "antigravity",
		Windows: []models.UsageWindow{
			{Label: "Session Gemini", UsedPercent: 2.0},
			{Label: "Weekly Gemini", UsedPercent: 24.0},
			{Label: "Weekly Claude & GPT", UsedPercent: 100.0},
		},
	}
	claude := models.ProviderUsage{
		ID: "claude",
		Windows: []models.UsageWindow{
			{Label: "Session", UsedPercent: 28.0},
			{Label: "Weekly", UsedPercent: 49.0},
		},
	}
	copilot := models.ProviderUsage{
		ID: "copilot",
		Windows: []models.UsageWindow{
			{Label: "Chat", UsedPercent: 5.9},
			{Label: "Completions", UsedPercent: 0.0},
		},
	}

	results := []models.ProviderUsage{claude, grok, antigravity, codex, copilot}
	sortResultsByUsage(results)

	// Usable providers (claude, antigravity, copilot) should come first in priority order,
	// demoting exhausted providers (grok, codex) to the bottom.
	expectedOrder := []string{"claude", "antigravity", "copilot", "grok", "codex"}
	for i, want := range expectedOrder {
		if results[i].ID != want {
			t.Errorf("at index %d: expected %s, got %s", i, want, results[i].ID)
		}
	}
}
