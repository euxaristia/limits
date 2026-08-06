package config

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"

	"github.com/euxaristia/limits/pkg/models"
)

func boolPtr(b bool) *bool {
	return &b
}

func GetDefaultConfigPath() string {
	if env := os.Getenv("LIMITS_CONFIG"); strings.TrimSpace(env) != "" {
		return env
	}
	if legacy := os.Getenv("CODEXBAR_CONFIG"); strings.TrimSpace(legacy) != "" {
		return legacy
	}

	xdgConfig := os.Getenv("XDG_CONFIG_HOME")
	homeDir, err := os.UserHomeDir()
	if err != nil {
		homeDir = "."
	}

	basePath := xdgConfig
	if strings.TrimSpace(basePath) == "" {
		basePath = filepath.Join(homeDir, ".config")
	}

	limitsPath := filepath.Join(basePath, "limits", "config.json")
	if _, err := os.Stat(limitsPath); err == nil {
		return limitsPath
	}

	codexbarPath := filepath.Join(basePath, "codexbar", "config.json")
	if _, err := os.Stat(codexbarPath); err == nil {
		return codexbarPath
	}

	return limitsPath
}

func CreateDefaultConfig() models.LimitsConfig {
	defaultProviders := []models.ProviderConfig{
		{ID: "codex", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "openai", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "claude", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "cursor", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "gemini", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "deepseek", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "openrouter", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "elevenlabs", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "antigravity", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "grok", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
		{ID: "copilot", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: ""},
	}
	return models.LimitsConfig{
		Version:   1,
		Providers: defaultProviders,
	}
}

func Load() models.LimitsConfig {
	path := GetDefaultConfigPath()
	data, err := os.ReadFile(path)
	if err != nil {
		defaultCfg := CreateDefaultConfig()
		_ = Save(defaultCfg)
		return defaultCfg
	}

	var cfg models.LimitsConfig
	if err := json.Unmarshal(data, &cfg); err != nil || len(cfg.Providers) == 0 {
		return CreateDefaultConfig()
	}

	hasGrok := false
	hasCopilot := false
	for _, p := range cfg.Providers {
		if strings.EqualFold(p.ID, "grok") {
			hasGrok = true
		}
		if strings.EqualFold(p.ID, "copilot") {
			hasCopilot = true
		}
	}

	if !hasGrok {
		cfg.Providers = append(cfg.Providers, models.ProviderConfig{
			ID: "grok", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: "",
		})
	}
	if !hasCopilot {
		cfg.Providers = append(cfg.Providers, models.ProviderConfig{
			ID: "copilot", Enabled: boolPtr(true), APIKey: "", CookieHeader: "", Region: "",
		})
	}

	return cfg
}

func Save(cfg models.LimitsConfig) error {
	path := GetDefaultConfigPath()
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return err
	}

	data, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(path, data, 0644)
}
