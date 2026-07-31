package credentials

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"

	"limits/pkg/config"
)

type CopilotCacheEntry struct {
	IsQuotaExceeded bool   `json:"isQuotaExceeded"`
	CheckedAtUTC    string `json:"checkedAtUtc"`
}

func getCopilotCachePath() string {
	configPath := config.GetDefaultConfigPath()
	dir := filepath.Dir(configPath)
	return filepath.Join(dir, "copilot-quota-cache.json")
}

func TryLoadCopilotCache() (bool, bool) {
	cachePath := getCopilotCachePath()
	data, err := os.ReadFile(cachePath)
	if err != nil {
		return false, false
	}

	var entry CopilotCacheEntry
	if err := json.Unmarshal(data, &entry); err != nil {
		return false, false
	}

	t, err := time.Parse(time.RFC3339, entry.CheckedAtUTC)
	if err != nil {
		return false, false
	}

	if time.Now().UTC().Sub(t.UTC()) < 20*time.Minute {
		return entry.IsQuotaExceeded, true
	}

	return false, false
}

func SaveCopilotCache(isQuotaExceeded bool) {
	cachePath := getCopilotCachePath()
	dir := filepath.Dir(cachePath)
	_ = os.MkdirAll(dir, 0755)

	entry := CopilotCacheEntry{
		IsQuotaExceeded: isQuotaExceeded,
		CheckedAtUTC:    time.Now().UTC().Format(time.RFC3339),
	}

	data, err := json.Marshal(entry)
	if err == nil {
		_ = os.WriteFile(cachePath, data, 0644)
	}
}
