package credentials

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
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

func TestForceRefreshViaCliHeadlessCandidateValidation(t *testing.T) {
	// Create a temp directory for fake CLIs
	cliDir, err := os.MkdirTemp("", "fake_cli_*")
	if err != nil {
		t.Fatalf("failed to create temp dir: %v", err)
	}
	defer os.RemoveAll(cliDir)

	// Create a temp directory for the token file (Claude reads ~/.claude/.credentials.json)
	tokenDir, err := os.MkdirTemp("", "token_test_*")
	if err != nil {
		t.Fatalf("failed to create token dir: %v", err)
	}
	defer os.RemoveAll(tokenDir)

	claudeDir := filepath.Join(tokenDir, ".claude")
	if err := os.MkdirAll(claudeDir, 0755); err != nil {
		t.Fatalf("failed to create .claude dir: %v", err)
	}
	tokenPath := filepath.Join(claudeDir, ".credentials.json")

	// Write an expired token initially
	expiredTime := time.Now().Add(-1 * time.Hour).Format(time.RFC3339)
	expiredJSON := `{"claudeAiOauth": {"accessToken": "expired_token", "expiresAt": "` + expiredTime + `"}}`
	if err := os.WriteFile(tokenPath, []byte(expiredJSON), 0600); err != nil {
		t.Fatalf("failed to write token file: %v", err)
	}

	// Create a single fake claude executable that models both candidates:
	// "claude auth status" runs but does not refresh the token (first candidate),
	// "claude --version" writes a fresh token (second candidate). It also
	// appends each invocation to a call log so the test can prove both ran.
	claudeCLI := filepath.Join(cliDir, "claude"+exeSuffix())
	freshTime := time.Now().Add(24 * time.Hour).Format(time.RFC3339Nano)
	callLogPath := filepath.Join(tokenDir, "claude_calls.log")
	claudeScript := `package main

import (
	"os"
	"strings"
	"time"
)

func main() {
	time.Sleep(10 * time.Millisecond)
	logPath := os.Getenv("CLAUDE_CALL_LOG")
	if logPath != "" {
		line := strings.Join(os.Args[1:], " ") + "\n"
		f, err := os.OpenFile(logPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0600)
		if err == nil {
			_, _ = f.WriteString(line)
			_ = f.Close()
		}
	}
	args := os.Args[1:]
	if len(args) == 0 || args[0] != "--version" {
		// First candidate: auth status. Run without refreshing the token.
		os.Exit(0)
	}
	// Second candidate: write a fresh token.
	freshJSON := ` + "`" + `{"claudeAiOauth": {"accessToken": "fresh_token", "expiresAt": "` + freshTime + `"}}` + "`" + `
	home := os.Getenv("HOME")
	if home == "" {
		home = os.Getenv("USERPROFILE")
	}
	tokenPath := home + string(os.PathSeparator) + ".claude" + string(os.PathSeparator) + ".credentials.json"
	if err := os.WriteFile(tokenPath, []byte(freshJSON), 0600); err != nil {
		os.Exit(1)
	}
	os.Exit(0)
}
`

	// Write the fake CLI
	if err := writeFakeCLI(t, claudeCLI, claudeScript); err != nil {
		t.Fatalf("failed to write claude CLI: %v", err)
	}

	// Set up environment - put our fake CLI first in PATH
	t.Setenv("PATH", cliDir+string(os.PathListSeparator)+os.Getenv("PATH"))
	t.Setenv("HOME", tokenDir)
	t.Setenv("USERPROFILE", tokenDir)
	t.Setenv("CLAUDE_CALL_LOG", callLogPath)

	// Initially token should be expired
	if IsClaudeWorking() {
		t.Fatal("expected IsClaudeWorking() to return false for expired token")
	}

	// Call ForceRefreshViaCliHeadless - should try "claude auth status" first
	// (token stays expired, loop continues), then "claude --version" (refreshes token).
	result := ForceRefreshViaCliHeadless(models.Claude)
	if !result {
		t.Fatal("expected ForceRefreshViaCliHeadless to return true after second candidate refreshes token")
	}

	// Token should now be valid
	if !IsClaudeWorking() {
		t.Fatal("expected IsClaudeWorking() to return true after refresh")
	}

	// Verify the refreshed token has the new access token
	refreshedTok, err := LoadClaudeToken()
	if err != nil {
		t.Fatalf("expected LoadClaudeToken to succeed after refresh: %v", err)
	}
	if refreshedTok.AccessToken != "fresh_token" {
		t.Errorf("expected fresh access token 'fresh_token', got '%s'", refreshedTok.AccessToken)
	}

	// Verify both candidates were checked, in order: the first left the token
	// unusable and the loop continued to the second, which refreshed it.
	callLog, err := os.ReadFile(callLogPath)
	if err != nil {
		t.Fatalf("expected call log to be written: %v", err)
	}
	got := string(callLog)
	want := "auth status\n--version\n"
	if got != want {
		t.Errorf("expected candidates checked in order %q, got %q", want, got)
	}
}

func exeSuffix() string {
	if runtime.GOOS == "windows" {
		return ".exe"
	}
	return ""
}

func writeFakeCLI(t *testing.T, path, script string) error {
	t.Helper()
	// Write the Go source
	srcPath := path + ".go"
	if err := os.WriteFile(srcPath, []byte(script), 0644); err != nil {
		return err
	}
	// Build it
	cmd := exec.Command("go", "build", "-o", path, srcPath)
	cmd.Env = append(os.Environ(), "CGO_ENABLED=0")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("build failed: %v\n%s", err, output)
	}
	return nil
}
