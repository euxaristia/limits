package credentials

import (
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/euxaristia/limits/pkg/models"
)

func TestGetCliCandidatesClaude(t *testing.T) {
	candidates := getCliCandidates(models.Claude)
	if len(candidates) == 0 {
		t.Fatalf("expected candidates for Claude, got none")
	}

	foundAuthStatus := false
	for _, c := range candidates {
		if c.cli == "claude" && len(c.args) >= 2 && c.args[0] == "auth" && c.args[1] == "status" {
			foundAuthStatus = true
			break
		}
	}
	if !foundAuthStatus {
		t.Errorf("expected candidate {cli: \"claude\", args: [\"auth\", \"status\"]}, got %v", candidates)
	}
}

func TestGetCliCandidatesGrok(t *testing.T) {
	candidates := getCliCandidates(models.Grok)
	if len(candidates) != 1 {
		t.Fatalf("expected one Grok refresh candidate, got %v", candidates)
	}

	candidate := candidates[0]
	if candidate.cli != "grok" || len(candidate.args) != 1 || candidate.args[0] != "models" {
		t.Errorf("expected authenticated Grok models probe, got %v", candidate)
	}
}

func TestLoadClaudeTokenAndIsWorking(t *testing.T) {
	tmpDir, err := os.MkdirTemp("", "claude_test_*")
	if err != nil {
		t.Fatalf("failed to create temp dir: %v", err)
	}
	defer os.RemoveAll(tmpDir)

	claudeDir := filepath.Join(tmpDir, ".claude")
	if err := os.MkdirAll(claudeDir, 0755); err != nil {
		t.Fatalf("failed to create .claude dir: %v", err)
	}

	credPath := filepath.Join(claudeDir, ".credentials.json")

	// 1. Expired token
	pastTime := time.Now().Add(-1 * time.Hour).Format(time.RFC3339)
	expiredJSON := `{"claudeAiOauth": {"accessToken": "test_expired_token", "expiresAt": "` + pastTime + `"}}`
	if err := os.WriteFile(credPath, []byte(expiredJSON), 0600); err != nil {
		t.Fatalf("failed to write cred file: %v", err)
	}

	t.Setenv("HOME", tmpDir)
	t.Setenv("USERPROFILE", tmpDir)

	tok, err := LoadClaudeToken()
	if err != nil {
		t.Fatalf("expected LoadClaudeToken to succeed, got %v", err)
	}
	if tok.AccessToken != "test_expired_token" {
		t.Errorf("expected access token 'test_expired_token', got '%s'", tok.AccessToken)
	}

	if IsClaudeWorking() {
		t.Errorf("expected IsClaudeWorking() to return false for expired token")
	}

	// 2. Fresh token with RFC3339Nano
	futureTime := time.Now().Add(24 * time.Hour).Format(time.RFC3339Nano)
	freshJSON := `{"claudeAiOauth": {"accessToken": "test_fresh_token", "expiresAt": "` + futureTime + `"}}`
	if err := os.WriteFile(credPath, []byte(freshJSON), 0600); err != nil {
		t.Fatalf("failed to write cred file: %v", err)
	}

	tok, err = LoadClaudeToken()
	if err != nil {
		t.Fatalf("expected LoadClaudeToken to succeed, got %v", err)
	}
	if tok.AccessToken != "test_fresh_token" {
		t.Errorf("expected access token 'test_fresh_token', got '%s'", tok.AccessToken)
	}

	if !IsClaudeWorking() {
		t.Errorf("expected IsClaudeWorking() to return true for fresh token")
	}
}
