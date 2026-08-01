package parsers_test

import (
	"fmt"
	"strings"
	"testing"
	"time"

	"limits/pkg/parsers"
)

func TestAntigravityParser_EmptyPayload(t *testing.T) {
	r := parsers.ParseAntigravityQuota([]byte("{}"))
	if len(r) != 0 {
		t.Fatalf("expected 0 buckets, got %d", len(r))
	}
}

func TestAntigravityParser_SingleGeminiBucket(t *testing.T) {
	reset := time.Now().UTC().Add(4 * time.Hour).Format(time.RFC3339)
	jsonStr := fmt.Sprintf(`{ "buckets": [ { "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.45, "resetTime": "%s", "tokenType": "WTUS" } ] }`, reset)

	r := parsers.ParseAntigravityQuota([]byte(jsonStr))
	if len(r) != 1 {
		t.Fatalf("expected 1 bucket, got %d", len(r))
	}

	b := r[0]
	if b.GroupLabel != "Gemini" {
		t.Errorf("expected GroupLabel Gemini, got %s", b.GroupLabel)
	}
	if b.Members != "Gemini Pro" {
		t.Errorf("expected Members 'Gemini Pro', got '%s'", b.Members)
	}
	if b.UsedPercent < 54.0 || b.UsedPercent > 56.0 {
		t.Errorf("expected UsedPercent ~55, got %f", b.UsedPercent)
	}
	if b.RemainingPercent < 44.0 || b.RemainingPercent > 46.0 {
		t.Errorf("expected RemainingPercent ~45, got %f", b.RemainingPercent)
	}
	if !strings.HasPrefix(b.ResetCountdown, "3h") && !strings.HasPrefix(b.ResetCountdown, "4h") {
		t.Errorf("expected countdown starting with 3h or 4h, got %s", b.ResetCountdown)
	}
}

func TestAntigravityParser_MultipleGeminiModelsGroup(t *testing.T) {
	reset := time.Now().UTC().Add(4 * time.Hour).Format(time.RFC3339)
	b1 := fmt.Sprintf(`{ "modelId": "gemini-2-5-pro", "remainingFraction": 0.50, "resetTime": "%s" }`, reset)
	b2 := fmt.Sprintf(`{ "modelId": "gemini-3-1-flash-lite", "remainingFraction": 0.20, "resetTime": "%s" }`, reset)
	jsonStr := fmt.Sprintf(`{ "buckets": [ %s, %s ] }`, b1, b2)

	r := parsers.ParseAntigravityQuota([]byte(jsonStr))
	if len(r) != 1 {
		t.Fatalf("expected 1 bucket, got %d", len(r))
	}

	b := r[0]
	if b.GroupLabel != "Gemini" {
		t.Errorf("expected Gemini, got %s", b.GroupLabel)
	}
	if len(strings.Split(b.Members, ",")) != 2 {
		t.Errorf("expected 2 members, got %s", b.Members)
	}
	if b.RemainingPercent != 20.0 {
		t.Errorf("expected 20%% remaining, got %f", b.RemainingPercent)
	}
}

func TestAntigravityParser_PlaceholdersFiltered(t *testing.T) {
	reset := time.Now().UTC().Add(4 * time.Hour).Format(time.RFC3339)
	b1 := fmt.Sprintf(`{ "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.50, "resetTime": "%s" }`, reset)
	b2 := `{ "modelId": "chat_23310", "remainingFraction": 1.0 }`
	b3 := `{ "modelId": "tab_flash_lite_preview", "remainingFraction": 1.0 }`
	jsonStr := fmt.Sprintf(`{ "buckets": [ %s, %s, %s ] }`, b1, b2, b3)

	r := parsers.ParseAntigravityQuota([]byte(jsonStr))
	if len(r) != 1 {
		t.Fatalf("expected 1 bucket, got %d", len(r))
	}
}

func TestAntigravityParser_GeminiOrderedBeforeClaude(t *testing.T) {
	reset := time.Now().UTC().Add(4 * time.Hour).Format(time.RFC3339)
	claude := fmt.Sprintf(`{ "modelId": "claude-opus-4-6", "remainingFraction": 0.5, "resetTime": "%s" }`, reset)
	gemini := fmt.Sprintf(`{ "modelId": "gemini-3-1-pro", "remainingFraction": 0.8, "resetTime": "%s" }`, reset)
	jsonStr := fmt.Sprintf(`{ "buckets": [ %s, %s ] }`, claude, gemini)

	r := parsers.ParseAntigravityQuota([]byte(jsonStr))
	if len(r) != 2 {
		t.Fatalf("expected 2 buckets, got %d", len(r))
	}
	if r[0].GroupLabel != "Gemini" {
		t.Errorf("expected first bucket to be Gemini, got %s", r[0].GroupLabel)
	}
	if r[1].GroupLabel != "Claude & GPT" {
		t.Errorf("expected second bucket to be Claude & GPT, got %s", r[1].GroupLabel)
	}
}

func TestClaudeParser_TwoBuckets(t *testing.T) {
	jsonStr := `{
		"five_hour": { "utilization": 45.0, "resets_at": "2026-07-31T20:00:00Z" },
		"seven_day": { "utilization": 80.0, "resets_at": "2026-08-05T00:00:00Z" }
	}`

	r := parsers.ParseClaudeUsage([]byte(jsonStr))
	if r.Session == nil || r.Weekly == nil {
		t.Fatalf("expected both session and weekly buckets")
	}
	if r.PrimaryUsed != 80.0 {
		t.Errorf("expected primary used 80 (weekly higher), got %f", r.PrimaryUsed)
	}
}

func TestEmailRedactor(t *testing.T) {
	redacted := parsers.RedactEmail("Grok CLI (cq4gppc54z@privaterelay.example.com)")
	if redacted != "Grok CLI (c***z@privaterelay.example.com)" {
		t.Errorf("unexpected redaction: %s", redacted)
	}

	redactedUser := parsers.RedactEmail("Account: user.name@example.com")
	if redactedUser != "Account: u***e@example.com" {
		t.Errorf("unexpected redaction: %s", redactedUser)
	}
}
