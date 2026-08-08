package fetchers

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"os/exec"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/euxaristia/limits/pkg/credentials"
	"github.com/euxaristia/limits/pkg/models"
	"github.com/euxaristia/limits/pkg/parsers"
)

var httpClient = &http.Client{
	Timeout: 15 * time.Second,
}

func setupHeaders(req *http.Request) {
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
	req.Header.Set("Accept", "application/json, text/plain, */*")
}

func singleWindow(
	provider models.UsageProvider,
	id, displayName string,
	used, limit float64,
	resetCountdown, status string,
	isMock, hasError bool,
	errorMessage, footer string,
) models.ProviderUsage {
	usedPct := 0.0
	if limit > 0.0 {
		usedPct = math.Max(0.0, math.Min(100.0, (used/limit)*100.0))
	}

	return models.ProviderUsage{
		Provider:    models.ProviderToString(provider),
		ID:          id,
		DisplayName: displayName,
		Windows: []models.UsageWindow{
			{
				Label:               "Quota",
				UsedPercent:         usedPct,
				ResetCountdown:      resetCountdown,
				WindowSeconds:       0,
				PercentTextOverride: nil,
			},
		},
		Status:       status,
		IsMock:       isMock,
		HasError:     hasError,
		ErrorMessage: parsers.RedactEmail(errorMessage),
		Footer:       parsers.RedactEmail(footer),
	}
}

type windowSpec struct {
	label           string
	pct             float64
	reset           string
	secs            int
	pctTextOverride *string
}

func multiWindow(
	provider models.UsageProvider,
	id, displayName string,
	specs []windowSpec,
	status string,
	isMock, hasError bool,
	errorMessage, footer string,
) models.ProviderUsage {
	windows := make([]models.UsageWindow, len(specs))
	for i, s := range specs {
		var redactedOverride *string
		if s.pctTextOverride != nil {
			r := parsers.RedactEmail(*s.pctTextOverride)
			redactedOverride = &r
		}
		windows[i] = models.UsageWindow{
			Label:               s.label,
			UsedPercent:         math.Max(0.0, math.Min(100.0, s.pct)),
			ResetCountdown:      s.reset,
			WindowSeconds:       s.secs,
			PercentTextOverride: redactedOverride,
		}
	}

	return models.ProviderUsage{
		Provider:     models.ProviderToString(provider),
		ID:           id,
		DisplayName:  displayName,
		Windows:      windows,
		Status:       status,
		IsMock:       isMock,
		HasError:     hasError,
		ErrorMessage: parsers.RedactEmail(errorMessage),
		Footer:       parsers.RedactEmail(footer),
	}
}

func GetUnconfiguredData(provider models.UsageProvider) models.ProviderUsage {
	name := models.GetDisplayName(provider)
	id := models.ProviderToString(provider)
	var msg string

	switch provider {
	case models.OpenAI:
		msg = "API key required. Set with 'limits config set-key openai <key>'"
	case models.Claude:
		msg = "API key or Claude CLI login required (~/.claude/.credentials.json)"
	case models.DeepSeek:
		msg = "API key required. Set with 'limits config set-key deepseek <key>'"
	case models.OpenRouter:
		msg = "API key required. Set with 'limits config set-key openrouter <key>'"
	case models.ElevenLabs:
		msg = "API key required. Set with 'limits config set-key elevenlabs <key>'"
	case models.Groq:
		msg = "API key required. Set with 'limits config set-key groq <key>'"
	case models.Bedrock:
		msg = "AWS credentials required"
	case models.Cursor:
		msg = "API key or token required"
	case models.Codex:
		msg = "ChatGPT login required. Run 'codex login' to create ~/.codex/auth.json"
	case models.Copilot:
		msg = "API key and organization required. Set with 'limits config set-key copilot <token>' and set the provider 'region' to your org name"
	default:
		msg = "Credentials or API key required"
	}

	return models.ProviderUsage{
		Provider:     models.ProviderToString(provider),
		ID:           id,
		DisplayName:  name,
		Windows:      []models.UsageWindow{},
		Status:       "unconfigured",
		IsMock:       false,
		HasError:     true,
		ErrorMessage: msg,
		Footer:       "",
	}
}

func fetchCodexUsage() models.ProviderUsage {
	provider := models.Codex
	name := models.GetDisplayName(provider)
	token, err := credentials.LoadCodexToken()
	if err != nil {
		return GetUnconfiguredData(provider)
	}

	req, err := http.NewRequest("GET", "https://chatgpt.com/backend-api/wham/usage", nil)
	if err != nil {
		return multiWindow(provider, "codex", name, nil, "degraded", false, true, err.Error(), "")
	}
	req.Header.Set("Authorization", "Bearer "+token.AccessToken)
	req.Header.Set("User-Agent", "codex-cli")
	req.Header.Set("Accept", "application/json")
	if strings.TrimSpace(token.AccountID) != "" {
		req.Header.Set("ChatGPT-Account-Id", token.AccountID)
	}

	resp, err := httpClient.Do(req)
	if err != nil {
		return multiWindow(provider, "codex", name, nil, "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return multiWindow(provider, "codex", name, nil, "degraded", false, true,
			fmt.Sprintf("Codex usage API returned HTTP %d; run 'codex login' if your session expired", resp.StatusCode), "")
	}

	usage, err := parsers.ParseCodexUsage(body, time.Now())
	if err != nil {
		return multiWindow(provider, "codex", name, nil, "degraded", false, true, err.Error(), "")
	}
	specs := make([]windowSpec, len(usage.Windows))
	for i, window := range usage.Windows {
		specs[i] = windowSpec{label: window.Label, pct: window.UsedPercent, reset: window.ResetCountdown, secs: window.WindowSeconds}
	}
	footer := "ChatGPT"
	if usage.PlanType != "" {
		footer += " " + usage.PlanType
	}
	return multiWindow(provider, "codex", name, specs, "healthy", false, false, "", footer)
}

// 1. OpenAI
func fetchOpenAIBalance(apiKey string) models.ProviderUsage {
	provider := models.OpenAI
	name := models.GetDisplayName(provider)

	req, err := http.NewRequest("GET", "https://api.openai.com/v1/dashboard/billing/credit_grants", nil)
	if err != nil {
		return singleWindow(provider, "openai", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(apiKey))

	resp, err := httpClient.Do(req)
	if err != nil {
		return singleWindow(provider, "openai", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		var root map[string]interface{}
		if err := json.Unmarshal(body, &root); err == nil {
			totalGranted, _ := root["total_granted"].(float64)
			totalUsed, _ := root["total_used"].(float64)
			totalAvailable, _ := root["total_available"].(float64)

			return singleWindow(
				provider, "openai", name,
				totalUsed, totalGranted,
				"Credit Expiry", "healthy", false, false, "",
				fmt.Sprintf("Available: $%.2f / $%.2f", totalAvailable, totalGranted),
			)
		}
	}
	return singleWindow(provider, "openai", name, 0, 100, "N/A", "degraded", false, true, fmt.Sprintf("API returned status %d", resp.StatusCode), "")
}

// 2. Claude Web API
func fetchClaudeUsage(cookieSource string) models.ProviderUsage {
	provider := models.Claude
	name := models.GetDisplayName(provider)

	sessionKey := cookieSource
	if idx := strings.Index(cookieSource, "sessionKey="); idx != -1 {
		start := idx + len("sessionKey=")
		endIdx := strings.Index(cookieSource[start:], ";")
		if endIdx == -1 {
			sessionKey = strings.TrimSpace(cookieSource[start:])
		} else {
			sessionKey = strings.TrimSpace(cookieSource[start : start+endIdx])
		}
	} else {
		sessionKey = strings.TrimSpace(cookieSource)
	}

	orgReq, err := http.NewRequest("GET", "https://claude.ai/api/organizations", nil)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(orgReq)
	orgReq.Header.Set("Cookie", "sessionKey="+sessionKey)

	orgResp, err := httpClient.Do(orgReq)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer orgResp.Body.Close()

	if orgResp.StatusCode != http.StatusOK {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, fmt.Sprintf("Orgs API returned HTTP %d (Invalid sessionKey?)", orgResp.StatusCode), "")
	}

	orgBody, _ := io.ReadAll(orgResp.Body)
	var orgs []map[string]interface{}
	if err := json.Unmarshal(orgBody, &orgs); err != nil || len(orgs) == 0 {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, "No organization UUID found", "")
	}

	orgID, _ := orgs[0]["uuid"].(string)
	usageURL := fmt.Sprintf("https://claude.ai/api/organizations/%s/usage", orgID)

	usageReq, err := http.NewRequest("GET", usageURL, nil)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(usageReq)
	usageReq.Header.Set("Cookie", "sessionKey="+sessionKey)

	usageResp, err := httpClient.Do(usageReq)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer usageResp.Body.Close()

	if usageResp.StatusCode != http.StatusOK {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, fmt.Sprintf("Usage API returned HTTP %d", usageResp.StatusCode), "")
	}

	usageBody, _ := io.ReadAll(usageResp.Body)
	parsed := parsers.ParseClaudeUsage(usageBody)

	var specs []windowSpec
	if parsed.Session != nil {
		specs = append(specs, windowSpec{label: "Session", pct: parsed.Session.Used, reset: parsed.Session.ResetCountdown, secs: 5 * 3600})
	}
	if parsed.Weekly != nil {
		specs = append(specs, windowSpec{label: "Weekly", pct: parsed.Weekly.Used, reset: parsed.Weekly.ResetCountdown, secs: 7 * 24 * 3600})
	}

	return multiWindow(provider, "claude", name, specs, "healthy", false, false, "", parsed.CostInfo)
}

// 2b. Claude OAuth
func fetchClaudeOAuthUsage(accessToken string) models.ProviderUsage {
	provider := models.Claude
	name := models.GetDisplayName(provider)

	req, err := http.NewRequest("GET", "https://api.anthropic.com/api/oauth/usage", nil)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(accessToken))
	req.Header.Set("anthropic-beta", "oauth-2025-04-20")

	resp, err := httpClient.Do(req)
	if err != nil {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return singleWindow(provider, "claude", name, 0, 100, "N/A", "degraded", false, true, fmt.Sprintf("OAuth API returned HTTP %d", resp.StatusCode), "")
	}

	body, _ := io.ReadAll(resp.Body)
	parsed := parsers.ParseClaudeUsage(body)

	var specs []windowSpec
	if parsed.Session != nil {
		specs = append(specs, windowSpec{label: "Session", pct: parsed.Session.Used, reset: parsed.Session.ResetCountdown, secs: 5 * 3600})
	}
	if parsed.Weekly != nil {
		specs = append(specs, windowSpec{label: "Weekly", pct: parsed.Weekly.Used, reset: parsed.Weekly.ResetCountdown, secs: 7 * 24 * 3600})
	}

	return multiWindow(provider, "claude", name, specs, "healthy", false, false, "", parsed.CostInfo)
}

// 3. DeepSeek
func fetchDeepSeekBalance(apiKey string) models.ProviderUsage {
	provider := models.DeepSeek
	name := models.GetDisplayName(provider)

	req, err := http.NewRequest("GET", "https://api.deepseek.com/user/balance", nil)
	if err != nil {
		return singleWindow(provider, "deepseek", name, 0, 0, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(apiKey))

	resp, err := httpClient.Do(req)
	if err != nil {
		return singleWindow(provider, "deepseek", name, 0, 0, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		var root map[string]interface{}
		if err := json.Unmarshal(body, &root); err == nil {
			if isAvailable, _ := root["is_available"].(bool); isAvailable {
				var totalBalance float64
				if infos, ok := root["balance_infos"].([]interface{}); ok {
					for _, info := range infos {
						if iMap, ok := info.(map[string]interface{}); ok {
							if balStr, ok := iMap["balance"].(string); ok {
								if bVal, err := strconv.ParseFloat(balStr, 64); err == nil {
									totalBalance += bVal
								}
							}
						}
					}
				}
				return singleWindow(provider, "deepseek", name, 0, totalBalance, "Never Resets", "healthy", false, false, "", fmt.Sprintf("Available: $%.2f", totalBalance))
			}
			return singleWindow(provider, "deepseek", name, 0, 0, "N/A", "degraded", false, true, "Account is unavailable", "")
		}
	}
	return singleWindow(provider, "deepseek", name, 0, 0, "N/A", "degraded", false, true, fmt.Sprintf("API returned status %d", resp.StatusCode), "")
}

// 4. OpenRouter
func fetchOpenRouterBalance(apiKey string) models.ProviderUsage {
	provider := models.OpenRouter
	name := models.GetDisplayName(provider)

	req, err := http.NewRequest("GET", "https://openrouter.ai/api/v1/auth/key", nil)
	if err != nil {
		return singleWindow(provider, "openrouter", name, 0, 0, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(apiKey))

	resp, err := httpClient.Do(req)
	if err != nil {
		return singleWindow(provider, "openrouter", name, 0, 0, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		var root map[string]interface{}
		if err := json.Unmarshal(body, &root); err == nil {
			if dataMap, ok := root["data"].(map[string]interface{}); ok {
				limit, _ := dataMap["limit"].(float64)
				usage, _ := dataMap["usage"].(float64)
				return singleWindow(provider, "openrouter", name, usage, limit, "Monthly Reset", "healthy", false, false, "", fmt.Sprintf("Used: $%.2f / $%.2f", usage, limit))
			}
		}
	}
	return singleWindow(provider, "openrouter", name, 0, 0, "N/A", "degraded", false, true, fmt.Sprintf("API returned status %d", resp.StatusCode), "")
}

// 5. Gemini Private Quota API
func fetchGeminiUsage() models.ProviderUsage {
	provider := models.Gemini
	name := models.GetDisplayName(provider)

	t, err := credentials.LoadGeminiToken()
	if err != nil || strings.TrimSpace(t.AccessToken) == "" {
		return GetUnconfiguredData(provider)
	}

	req, err := http.NewRequest("POST", "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota", bytes.NewBufferString("{}"))
	if err != nil {
		return singleWindow(provider, "gemini", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+t.AccessToken)
	req.Header.Set("Content-Type", "application/json")

	resp, err := httpClient.Do(req)
	if err != nil {
		return singleWindow(provider, "gemini", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		var quotaRoot map[string]interface{}
		if err := json.Unmarshal(body, &quotaRoot); err == nil {
			lowestFraction := 1.0
			resetTime := "Daily Quota"

			if buckets, ok := quotaRoot["buckets"].([]interface{}); ok {
				for _, b := range buckets {
					if bMap, ok := b.(map[string]interface{}); ok {
						if frac, ok := bMap["remainingFraction"].(float64); ok {
							if frac < lowestFraction {
								lowestFraction = frac
							}
						}
						if rt, ok := bMap["resetTime"].(string); ok && rt != "" {
							resetTime = parsers.FormatCountdown(rt)
						}
					}
				}
			}

			usedPercent := (1.0 - lowestFraction) * 100.0
			return singleWindow(provider, "gemini", name, usedPercent, 100.0, resetTime, "healthy", false, false, "", "Google Code Assist Quota")
		}
	}

	return singleWindow(provider, "gemini", name, 0, 100, "N/A", "degraded", false, true, fmt.Sprintf("Quota API returned status %d", resp.StatusCode), "")
}

// 6. Antigravity Usage
func fetchAntigravityUsage() models.ProviderUsage {
	provider := models.Antigravity
	name := models.GetDisplayName(provider)

	token, err := credentials.LoadAntigravityToken()
	if err != nil {
		return singleWindow(provider, "antigravity", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}

	callQuotaEndpoint := func(endpointURL string) (*http.Response, error) {
		req, err := http.NewRequest("POST", endpointURL, bytes.NewBufferString("{}"))
		if err != nil {
			return nil, err
		}
		setupHeaders(req)
		req.Header.Set("User-Agent", "antigravity")
		req.Header.Set("Authorization", "Bearer "+token.AccessToken)
		req.Header.Set("Content-Type", "application/json")
		return httpClient.Do(req)
	}

	resp, err := callQuotaEndpoint("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary")
	if err != nil || resp.StatusCode != http.StatusOK {
		if resp != nil {
			resp.Body.Close()
		}
		resp, err = callQuotaEndpoint("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")
	}

	if err != nil {
		return singleWindow(provider, "antigravity", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		buckets := parsers.ParseAntigravityQuota(body)

		if len(buckets) == 0 {
			return singleWindow(provider, "antigravity", name, 0, 100, "N/A", "healthy", false, false, "", fmt.Sprintf("Antigravity (%s) - no quota data", token.AuthMethod))
		}

		var specs []windowSpec
		var allMembers []string

		for _, b := range buckets {
			tf := ""
			if b.Timeframe != "" && b.Timeframe != "Quota" {
				tf = b.Timeframe + " "
			}
			membersList := strings.Split(b.Members, ",")
			mCount := len(membersList)
			plural := "s"
			if mCount == 1 {
				plural = ""
			}
			label := fmt.Sprintf("%s%s (%d model%s)", tf, b.GroupLabel, mCount, plural)

			var remText string
			if b.RemainingPercent < 10.0 {
				remText = fmt.Sprintf("%.1f%% remaining", b.RemainingPercent)
			} else {
				remText = fmt.Sprintf("%d%% remaining", int(math.Round(b.RemainingPercent)))
			}

			specs = append(specs, windowSpec{
				label:           label,
				pct:             b.UsedPercent,
				reset:           b.ResetCountdown,
				secs:            0,
				pctTextOverride: &remText,
			})

			for _, m := range membersList {
				allMembers = append(allMembers, strings.TrimSpace(m))
			}
		}

		// Dedupe members
		uniqueMembers := make(map[string]bool)
		for _, m := range allMembers {
			if m != "" {
				uniqueMembers[m] = true
			}
		}

		accountLabel := token.AuthMethod
		if token.Email != "" {
			accountLabel = fmt.Sprintf("%s, %s", token.Email, token.AuthMethod)
		}

		groupPlural := "s"
		if len(buckets) == 1 {
			groupPlural = ""
		}
		modelPlural := "s"
		if len(uniqueMembers) == 1 {
			modelPlural = ""
		}

		footer := fmt.Sprintf("Antigravity (%s) - %d group%s, %d model%s", accountLabel, len(buckets), groupPlural, len(uniqueMembers), modelPlural)
		return multiWindow(provider, "antigravity", name, specs, "healthy", false, false, "", footer)
	}

	errBody, _ := io.ReadAll(resp.Body)
	errMsg := fmt.Sprintf("Antigravity API returned status %d: %s", resp.StatusCode, string(errBody))
	if len(errMsg) > 200 {
		errMsg = errMsg[:200] + "..."
	}
	return singleWindow(provider, "antigravity", name, 0, 100, "N/A", "degraded", false, true, errMsg, "")
}

// 7. Grok
func fetchGrokUsage(tokenOpt *credentials.GrokToken, apiKey string) models.ProviderUsage {
	provider := models.Grok
	name := models.GetDisplayName(provider)

	bearerToken := apiKey
	if tokenOpt != nil {
		bearerToken = tokenOpt.AccessToken
	}

	if strings.TrimSpace(bearerToken) == "" {
		return GetUnconfiguredData(provider)
	}

	userReq, err := http.NewRequest("GET", "https://cli-chat-proxy.grok.com/v1/user", nil)
	if err != nil {
		return singleWindow(provider, "grok", name, 0, 100, "N/A", "degraded", false, true, err.Error(), "")
	}
	setupHeaders(userReq)
	userReq.Header.Set("User-Agent", "grok/0.2.111")
	userReq.Header.Set("x-grok-client-version", "0.2.111")
	userReq.Header.Set("Authorization", "Bearer "+strings.TrimSpace(bearerToken))

	userRes, err := httpClient.Do(userReq)
	if err != nil || userRes.StatusCode != http.StatusOK {
		if userRes != nil {
			userRes.Body.Close()
		}
		return singleWindow(provider, "grok", name, 0, 100, "N/A", "degraded", false, true, "Grok API request failed", "")
	}
	defer userRes.Body.Close()

	userBody, _ := io.ReadAll(userRes.Body)
	var root map[string]interface{}
	_ = json.Unmarshal(userBody, &root)

	email := ""
	if tokenOpt != nil && tokenOpt.Email != "" {
		email = tokenOpt.Email
	} else if eStr, ok := root["email"].(string); ok {
		email = eStr
	}

	usedPct := 0.0
	resetCountdown := "Active"
	if tokenOpt != nil && tokenOpt.ExpiresAt != nil {
		resetCountdown = parsers.FormatCountdown(tokenOpt.ExpiresAt.Format(time.RFC3339))
	}

	// Billing query
	billingReq, err := http.NewRequest("GET", "https://cli-chat-proxy.grok.com/v1/billing?format=credits", nil)
	if err == nil {
		setupHeaders(billingReq)
		billingReq.Header.Set("User-Agent", "grok/0.2.111")
		billingReq.Header.Set("x-grok-client-version", "0.2.111")
		billingReq.Header.Set("Authorization", "Bearer "+strings.TrimSpace(bearerToken))

		billingRes, err := httpClient.Do(billingReq)
		if err == nil && billingRes.StatusCode == http.StatusOK {
			bBody, _ := io.ReadAll(billingRes.Body)
			var bRoot map[string]interface{}
			if err := json.Unmarshal(bBody, &bRoot); err == nil {
				if cfgMap, ok := bRoot["config"].(map[string]interface{}); ok {
					if pctNum, ok := cfgMap["creditUsagePercent"].(float64); ok {
						usedPct = pctNum
					}
					if periodMap, ok := cfgMap["currentPeriod"].(map[string]interface{}); ok {
						if endStr, ok := periodMap["end"].(string); ok && endStr != "" {
							resetCountdown = parsers.FormatCountdown(endStr)
						}
					}
				}
			}
			billingRes.Body.Close()
		}
	}

	details := fmt.Sprintf("%.0f%% used", usedPct)
	specs := []windowSpec{
		{label: "Weekly", pct: usedPct, reset: resetCountdown, secs: 7 * 24 * 3600, pctTextOverride: &details},
	}

	accountStr := "Active"
	if email != "" {
		accountStr = email
	}
	footer := fmt.Sprintf("Grok CLI (%s)", accountStr)
	return multiWindow(provider, "grok", name, specs, "healthy", false, false, "", footer)
}

// 8. Copilot

// copilotQuotaLabels names the quotas GitHub reports, in the order we show
// them. Anything GitHub adds later is appended after these, alphabetically.
var copilotQuotaLabels = []struct{ id, label string }{
	{"premium_interactions", "Premium"},
	{"chat", "Chat"},
	{"completions", "Completions"},
}

var copilotPlanNames = map[string]string{
	"free_limited_copilot": "Copilot Free",
	"copilot_for_business": "Copilot Business",
	"copilot_enterprise":   "Copilot Enterprise",
	"copilot_pro":          "Copilot Pro",
}

type copilotQuota struct {
	Unlimited        bool     `json:"unlimited"`
	HasQuota         bool     `json:"has_quota"`
	PercentRemaining float64  `json:"percent_remaining"`
	Entitlement      float64  `json:"entitlement"`
	Remaining        *float64 `json:"remaining"`
}

type copilotUser struct {
	Login         string                  `json:"login"`
	AccessTypeSku string                  `json:"access_type_sku"`
	CopilotPlan   string                  `json:"copilot_plan"`
	QuotaResetUTC string                  `json:"quota_reset_date_utc"`
	Quotas        map[string]copilotQuota `json:"quota_snapshots"`
}

// copilotWindows turns the reported quotas into display rows, in a stable
// order, and reports whether any of them is spent.
func copilotWindows(payload copilotUser) ([]windowSpec, bool) {
	resetCountdown := ""
	if strings.TrimSpace(payload.QuotaResetUTC) != "" {
		resetCountdown = parsers.FormatCountdown(payload.QuotaResetUTC)
	}

	ordered := make([]string, 0, len(payload.Quotas))
	labels := map[string]string{}
	for _, known := range copilotQuotaLabels {
		if _, ok := payload.Quotas[known.id]; ok {
			ordered = append(ordered, known.id)
			labels[known.id] = known.label
		}
	}
	extras := make([]string, 0, len(payload.Quotas))
	for id := range payload.Quotas {
		if _, named := labels[id]; !named {
			extras = append(extras, id)
		}
	}
	sort.Strings(extras)
	ordered = append(ordered, extras...)

	var specs []windowSpec
	exhausted := false
	for _, id := range ordered {
		q := payload.Quotas[id]
		// GitHub lists every quota it knows about, including ones this plan has
		// no entitlement to. Showing those as 0% would imply they are usable.
		if !q.HasQuota && !q.Unlimited {
			continue
		}

		label := labels[id]
		if label == "" && id != "" {
			label = strings.ToUpper(id[:1]) + strings.ReplaceAll(id[1:], "_", " ")
		}

		if q.Unlimited {
			text := "Unlimited"
			specs = append(specs, windowSpec{label: label, pct: 0.0, reset: resetCountdown, secs: 30 * 24 * 3600, pctTextOverride: &text})
			continue
		}

		pct, used := q.usedOf()
		if pct >= 100.0 {
			exhausted = true
		}
		text := fmt.Sprintf("%.0f / %.0f (%.1f%% used)", used, q.Entitlement, pct)
		specs = append(specs, windowSpec{label: label, pct: pct, reset: resetCountdown, secs: 30 * 24 * 3600, pctTextOverride: &text})
	}

	return specs, exhausted
}

// usedOf reports how much of the quota is spent, as a percentage and as a
// count. GitHub sends what is left, not what is gone.
func (q copilotQuota) usedOf() (float64, float64) {
	pct := math.Max(0.0, math.Min(100.0, 100.0-q.PercentRemaining))
	used := q.Entitlement * pct / 100.0
	if q.Remaining != nil {
		used = q.Entitlement - *q.Remaining
	}
	return pct, math.Max(0.0, used)
}

func fetchCopilotUsage(cfg models.ProviderConfig) models.ProviderUsage {
	provider := models.Copilot
	name := models.GetDisplayName(provider)

	bearer := cfg.APIKey
	if strings.TrimSpace(bearer) == "" {
		ghCmd := exec.Command("gh", "auth", "token")
		credentials.PrepareHeadless(ghCmd)
		out, err := ghCmd.Output()
		if err == nil {
			bearer = strings.TrimSpace(string(out))
		}
	}

	bearer = strings.TrimSpace(bearer)
	if bearer == "" {
		return GetUnconfiguredData(provider)
	}

	// The documented REST API only reports Copilot seats for organisations. The
	// per-user quota lives on the endpoint the editor plugins read, which is
	// undocumented and can change without notice, hence the explicit degraded
	// state below rather than a fabricated healthy one.
	req, err := http.NewRequest("GET", "https://api.github.com/copilot_internal/user", nil)
	if err != nil {
		return copilotUnavailable(provider, name, err.Error())
	}
	setupHeaders(req)
	req.Header.Set("Authorization", "Bearer "+bearer)

	resp, err := httpClient.Do(req)
	if err != nil {
		return copilotUnavailable(provider, name, err.Error())
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return copilotUnavailable(provider, name, fmt.Sprintf("GitHub returned %s", resp.Status))
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return copilotUnavailable(provider, name, err.Error())
	}

	var payload copilotUser
	if err := json.Unmarshal(body, &payload); err != nil {
		return copilotUnavailable(provider, name, "unexpected response shape")
	}

	specs, exhausted := copilotWindows(payload)
	if len(specs) == 0 {
		return copilotUnavailable(provider, name, "no quotas reported for this account")
	}

	userLabel := payload.Login
	if userLabel == "" {
		userLabel = "user"
	}
	planLabel := copilotPlanNames[payload.AccessTypeSku]
	if planLabel == "" {
		planLabel = payload.CopilotPlan
	}

	footer := fmt.Sprintf("GitHub Copilot (User: %s)", userLabel)
	if planLabel != "" {
		footer = fmt.Sprintf("GitHub Copilot (User: %s, Plan: %s)", userLabel, planLabel)
	}
	if exhausted {
		footer += " - Quota Exceeded"
	}

	return multiWindow(provider, "copilot", name, specs, "healthy", false, false, "", footer)
}

func copilotUnavailable(provider models.UsageProvider, name, reason string) models.ProviderUsage {
	return multiWindow(provider, "copilot", name, nil, "degraded", false, true,
		fmt.Sprintf("Copilot quota unavailable: %s", reason), "")
}

func Fetch(cfg models.ProviderConfig) models.ProviderUsage {
	provider := models.ProviderFromString(cfg.ID)
	hasAPIKey := strings.TrimSpace(cfg.APIKey) != ""
	hasCookie := strings.TrimSpace(cfg.CookieHeader) != ""

	switch provider {
	case models.Codex:
		return fetchCodexUsage()
	case models.OpenAI:
		if hasAPIKey {
			return fetchOpenAIBalance(cfg.APIKey)
		}
		return GetUnconfiguredData(provider)
	case models.Claude:
		if hasCookie || hasAPIKey {
			token := cfg.APIKey
			if hasCookie {
				token = cfg.CookieHeader
			}
			return fetchClaudeUsage(token)
		}
		_ = credentials.EnsureTokenReady(models.Claude)
		if t, err := credentials.LoadClaudeToken(); err == nil && strings.TrimSpace(t.AccessToken) != "" {
			res := fetchClaudeOAuthUsage(t.AccessToken)
			if res.HasError && (strings.Contains(res.ErrorMessage, "401") || strings.Contains(res.ErrorMessage, "Unauthorized")) {
				if credentials.ForceRefreshViaCliHeadless(models.Claude) {
					if freshToken, err := credentials.LoadClaudeToken(); err == nil && strings.TrimSpace(freshToken.AccessToken) != "" {
						return fetchClaudeOAuthUsage(freshToken.AccessToken)
					}
				}
			}
			return res
		}
		return GetUnconfiguredData(provider)
	case models.DeepSeek:
		if hasAPIKey {
			return fetchDeepSeekBalance(cfg.APIKey)
		}
		return GetUnconfiguredData(provider)
	case models.OpenRouter:
		if hasAPIKey {
			return fetchOpenRouterBalance(cfg.APIKey)
		}
		return GetUnconfiguredData(provider)
	case models.Copilot:
		_ = credentials.EnsureTokenReady(models.Copilot)
		return fetchCopilotUsage(cfg)
	case models.Gemini:
		_ = credentials.EnsureTokenReady(models.Gemini)
		return fetchGeminiUsage()
	case models.Antigravity:
		_ = credentials.EnsureTokenReady(models.Antigravity)
		return fetchAntigravityUsage()
	case models.Grok:
		_ = credentials.EnsureTokenReady(models.Grok)
		tokenOpt, _ := credentials.LoadGrokToken()
		return fetchGrokUsage(tokenOpt, cfg.APIKey)
	default:
		return GetUnconfiguredData(provider)
	}
}

func FetchAllConcurrent(configs []models.ProviderConfig) []models.ProviderUsage {
	var wg sync.WaitGroup
	results := make([]models.ProviderUsage, len(configs))

	for i, cfg := range configs {
		wg.Add(1)
		go func(idx int, c models.ProviderConfig) {
			defer wg.Done()
			results[idx] = Fetch(c)
		}(i, cfg)
	}

	wg.Wait()
	return results
}
