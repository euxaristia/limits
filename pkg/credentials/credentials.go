package credentials

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"limits/pkg/models"
)

type Token struct {
	AccessToken  string
	RefreshToken string
	Expiry       *time.Time
	AuthMethod   string
	Email        string
}

func parseAntigravityTokenJSON(data []byte) (*Token, error) {
	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}

	tokenMap, ok := root["token"].(map[string]interface{})
	if !ok {
		return nil, errors.New("missing 'token' object")
	}

	accessToken, _ := tokenMap["access_token"].(string)
	if strings.TrimSpace(accessToken) == "" {
		return nil, errors.New("empty access_token")
	}

	refreshToken, _ := tokenMap["refresh_token"].(string)

	var expiry *time.Time
	if expiryStr, ok := tokenMap["expiry"].(string); ok && expiryStr != "" {
		if t, err := time.Parse(time.RFC3339, expiryStr); err == nil {
			expiry = &t
		}
	}

	authMethod, _ := root["auth_method"].(string)
	if authMethod == "" {
		authMethod = "unknown"
	}

	var email string
	if idToken, ok := root["id_token"].(string); ok && idToken != "" {
		parts := strings.Split(idToken, ".")
		if len(parts) >= 2 {
			payloadBase64 := parts[1]
			payloadBase64 = strings.ReplaceAll(payloadBase64, "-", "+")
			payloadBase64 = strings.ReplaceAll(payloadBase64, "_", "/")
			switch len(payloadBase64) % 4 {
			case 2:
				payloadBase64 += "=="
			case 3:
				payloadBase64 += "="
			}
			if decoded, err := base64.StdEncoding.DecodeString(payloadBase64); err == nil {
				var payloadMap map[string]interface{}
				if err := json.Unmarshal(decoded, &payloadMap); err == nil {
					email, _ = payloadMap["email"].(string)
				}
			}
		}
	}

	return &Token{
		AccessToken:  accessToken,
		RefreshToken: refreshToken,
		Expiry:       expiry,
		AuthMethod:   authMethod,
		Email:        email,
	}, nil
}

// loadAntigravityKeyringJSON returns the raw credential JSON the Antigravity CLI
// stores in the OS keyring. Recent versions (1.1.x) keep the live token there and
// leave the on-disk token file behind as a stale artifact. The value is written by
// go-keyring, which base64-encodes payloads behind a "go-keyring-base64:" marker.
func loadAntigravityKeyringJSON() ([]byte, error) {
	const service, account = "gemini", "antigravity"

	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "darwin":
		cmd = exec.Command("security", "find-generic-password", "-s", service, "-a", account, "-w")
	case "linux":
		cmd = exec.Command("secret-tool", "lookup", "service", service, "username", account)
	default:
		return nil, errors.New("keyring lookup unsupported on " + runtime.GOOS)
	}

	out, err := cmd.Output()
	if err != nil {
		return nil, err
	}

	raw := strings.TrimSpace(string(out))
	if raw == "" {
		return nil, errors.New("empty keyring entry")
	}
	if encoded, ok := strings.CutPrefix(raw, "go-keyring-base64:"); ok {
		decoded, err := base64.StdEncoding.DecodeString(encoded)
		if err != nil {
			return nil, err
		}
		return decoded, nil
	}
	return []byte(raw), nil
}

func LoadAntigravityToken() (*Token, error) {
	if data, err := loadAntigravityKeyringJSON(); err == nil {
		if token, err := parseAntigravityTokenJSON(data); err == nil {
			return token, nil
		}
	}

	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}

	dirs := []string{
		filepath.Join(homeDir, ".gemini", "antigravity-cli"),
		filepath.Join(homeDir, ".config", "antigravity"),
		filepath.Join(homeDir, ".config", "agy"),
	}
	// The agy CLI writes its OAuth token to "antigravity-oauth-token"; older and
	// alternate installs use "credentials.json".
	names := []string{"antigravity-oauth-token", "credentials.json"}

	var paths []string
	for _, d := range dirs {
		for _, n := range names {
			paths = append(paths, filepath.Join(d, n))
		}
	}

	for _, p := range paths {
		if data, err := os.ReadFile(p); err == nil {
			if token, err := parseAntigravityTokenJSON(data); err == nil {
				return token, nil
			}
		}
	}

	return nil, errors.New("no Antigravity credentials found")
}

func IsAntigravityWorking() bool {
	t, err := LoadAntigravityToken()
	if err != nil || strings.TrimSpace(t.AccessToken) == "" {
		return false
	}
	if t.Expiry != nil {
		return t.Expiry.UTC().After(time.Now().UTC().Add(30 * time.Second))
	}
	return true
}

type GrokToken struct {
	AccessToken string
	Email       string
	ExpiresAt   *time.Time
	AuthMode    string
}

func LoadGrokToken() (*GrokToken, error) {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}
	p := filepath.Join(homeDir, ".grok", "auth.json")
	data, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}

	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}

	for _, val := range root {
		entry, ok := val.(map[string]interface{})
		if !ok {
			continue
		}
		key, _ := entry["key"].(string)
		if key == "" {
			continue
		}
		email, _ := entry["email"].(string)
		authMode, _ := entry["auth_mode"].(string)
		if authMode == "" {
			authMode = "oidc"
		}

		var expiresAt *time.Time
		if expVal := entry["expires_at"]; expVal != nil {
			if s, ok := expVal.(string); ok {
				if t, err := time.Parse(time.RFC3339, s); err == nil {
					expiresAt = &t
				}
			} else if num, ok := expVal.(float64); ok {
				unixVal := int64(num)
				var t time.Time
				if unixVal > 100000000000 {
					t = time.UnixMilli(unixVal).UTC()
				} else {
					t = time.Unix(unixVal, 0).UTC()
				}
				expiresAt = &t
			}
		}
		return &GrokToken{
			AccessToken: key,
			Email:       email,
			ExpiresAt:   expiresAt,
			AuthMode:    authMode,
		}, nil
	}

	return nil, errors.New("no valid grok token found")
}

func IsGrokWorking() bool {
	t, err := LoadGrokToken()
	if err != nil || strings.TrimSpace(t.AccessToken) == "" {
		return false
	}
	if t.ExpiresAt != nil {
		return t.ExpiresAt.UTC().After(time.Now().UTC().Add(30 * time.Second))
	}
	return true
}

type ClaudeToken struct {
	AccessToken string
	ExpiresAt   *time.Time
}

// CodexToken is the ChatGPT session recorded by the Codex CLI after `codex login`.
// It is intentionally limited to the fields needed to read the account usage endpoint.
type CodexToken struct {
	AccessToken string
	AccountID   string
	PlanType    string
}

// LoadCodexToken reads the existing Codex CLI login without copying it into limits'
// configuration. ChatGPT authentication is not an OpenAI API key.
func LoadCodexToken() (*CodexToken, error) {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}

	data, err := os.ReadFile(filepath.Join(homeDir, ".codex", "auth.json"))
	if err != nil {
		return nil, err
	}

	var root struct {
		Tokens struct {
			AccessToken string `json:"access_token"`
			AccountID   string `json:"account_id"`
			PlanType    string `json:"plan_type"`
		} `json:"tokens"`
	}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}
	if strings.TrimSpace(root.Tokens.AccessToken) == "" {
		return nil, errors.New("missing Codex ChatGPT access token")
	}

	return &CodexToken{
		AccessToken: root.Tokens.AccessToken,
		AccountID:   root.Tokens.AccountID,
		PlanType:    root.Tokens.PlanType,
	}, nil
}
func LoadClaudeToken() (*ClaudeToken, error) {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}
	p := filepath.Join(homeDir, ".claude", ".credentials.json")
	data, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}

	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}

	oauthMap, ok := root["claudeAiOauth"].(map[string]interface{})
	if !ok {
		return nil, errors.New("missing claudeAiOauth")
	}

	token, _ := oauthMap["accessToken"].(string)
	if strings.TrimSpace(token) == "" {
		return nil, errors.New("missing accessToken")
	}

	var expiresAt *time.Time
	expVal := oauthMap["expiresAt"]
	if expVal == nil {
		expVal = oauthMap["expires_at"]
	}
	if expVal != nil {
		if s, ok := expVal.(string); ok {
			if t, err := time.Parse(time.RFC3339, s); err == nil {
				expiresAt = &t
			}
		} else if num, ok := expVal.(float64); ok {
			unixVal := int64(num)
			var t time.Time
			if unixVal > 100000000000 {
				t = time.UnixMilli(unixVal).UTC()
			} else {
				t = time.Unix(unixVal, 0).UTC()
			}
			expiresAt = &t
		}
	}

	return &ClaudeToken{
		AccessToken: token,
		ExpiresAt:   expiresAt,
	}, nil
}

func IsClaudeWorking() bool {
	t, err := LoadClaudeToken()
	if err != nil || strings.TrimSpace(t.AccessToken) == "" {
		return false
	}
	if t.ExpiresAt != nil {
		return t.ExpiresAt.UTC().After(time.Now().UTC().Add(30 * time.Second))
	}
	return true
}

type GeminiToken struct {
	AccessToken string
	ExpiresAt   *time.Time
}

func LoadGeminiToken() (*GeminiToken, error) {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}
	p := filepath.Join(homeDir, ".gemini", "oauth_creds.json")
	data, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}

	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, err
	}

	token, _ := root["access_token"].(string)
	if strings.TrimSpace(token) == "" {
		return nil, errors.New("missing access_token")
	}

	var expiresAt *time.Time
	expVal := root["expiry"]
	if expVal == nil {
		expVal = root["expires_at"]
	}
	if expVal != nil {
		if s, ok := expVal.(string); ok {
			if t, err := time.Parse(time.RFC3339, s); err == nil {
				expiresAt = &t
			}
		} else if num, ok := expVal.(float64); ok {
			unixVal := int64(num)
			var t time.Time
			if unixVal > 100000000000 {
				t = time.UnixMilli(unixVal).UTC()
			} else {
				t = time.Unix(unixVal, 0).UTC()
			}
			expiresAt = &t
		}
	}

	return &GeminiToken{
		AccessToken: token,
		ExpiresAt:   expiresAt,
	}, nil
}

func IsGeminiWorking() bool {
	t, err := LoadGeminiToken()
	if err != nil || strings.TrimSpace(t.AccessToken) == "" {
		return false
	}
	if t.ExpiresAt != nil {
		return t.ExpiresAt.UTC().After(time.Now().UTC().Add(30 * time.Second))
	}
	return true
}

func IsCopilotWorking() bool {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		return false
	}
	p := filepath.Join(homeDir, ".config", "gh", "hosts.yml")
	_, err = os.Stat(p)
	return err == nil
}

func IsCliAvailable(cmdName string) bool {
	if strings.TrimSpace(cmdName) == "" {
		return false
	}
	if filepath.IsAbs(cmdName) {
		_, err := os.Stat(cmdName)
		return err == nil
	}

	pathVar := os.Getenv("PATH")
	if strings.TrimSpace(pathVar) == "" {
		return false
	}

	pathSep := string(os.PathListSeparator)
	exts := []string{""}
	if runtime.GOOS == "windows" {
		pathext := os.Getenv("PATHEXT")
		if strings.TrimSpace(pathext) != "" {
			exts = strings.Split(pathext, ";")
		} else {
			exts = []string{".exe", ".cmd", ".bat", ".com"}
		}
	}

	dirs := strings.Split(pathVar, pathSep)
	for _, dir := range dirs {
		for _, ext := range exts {
			full := filepath.Join(dir, cmdName+ext)
			if _, err := os.Stat(full); err == nil {
				return true
			}
		}
	}
	return false
}

func RunCliHeadless(cliName string, args []string, timeoutSec float64) bool {
	ctxCmd := exec.Command(cliName, args...)
	PrepareHeadless(ctxCmd)
	done := make(chan error, 1)

	if err := ctxCmd.Start(); err != nil {
		return false
	}

	go func() {
		done <- ctxCmd.Wait()
	}()

	select {
	case <-time.After(time.Duration(timeoutSec * float64(time.Second))):
		_ = ctxCmd.Process.Kill()
		return false
	case err := <-done:
		return err == nil
	}
}

func IsTokenWorking(provider models.UsageProvider) bool {
	switch provider {
	case models.Grok:
		return IsGrokWorking()
	case models.Antigravity:
		return IsAntigravityWorking()
	case models.Claude:
		return IsClaudeWorking()
	case models.Gemini:
		return IsGeminiWorking()
	case models.Copilot:
		return IsCopilotWorking()
	default:
		return true
	}
}

type candidate struct {
	cli  string
	args []string
}

func getCliCandidates(provider models.UsageProvider) []candidate {
	switch provider {
	case models.Grok:
		return []candidate{
			{cli: "grok", args: []string{"--version"}},
			{cli: "grok", args: []string{"auth", "status"}},
		}
	case models.Antigravity:
		return []candidate{
			{cli: "agy", args: []string{"--version"}},
			{cli: "antigravity", args: []string{"--version"}},
		}
	case models.Claude:
		return []candidate{
			{cli: "claude", args: []string{"--version"}},
		}
	case models.Gemini:
		return []candidate{
			{cli: "gemini", args: []string{"--version"}},
			{cli: "gcloud", args: []string{"auth", "print-access-token"}},
		}
	case models.Copilot:
		return []candidate{
			{cli: "copilot", args: []string{"--version"}},
			{cli: "gh", args: []string{"auth", "token"}},
		}
	default:
		return nil
	}
}

func ForceRefreshViaCliHeadless(provider models.UsageProvider) bool {
	candidates := getCliCandidates(provider)
	for _, c := range candidates {
		if IsCliAvailable(c.cli) {
			_ = RunCliHeadless(c.cli, c.args, 10.0)
			return IsTokenWorking(provider)
		}
	}
	return false
}

func EnsureTokenReady(provider models.UsageProvider) bool {
	if IsTokenWorking(provider) {
		return true
	}
	return ForceRefreshViaCliHeadless(provider)
}
