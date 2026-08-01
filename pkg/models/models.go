package models

type UsageProvider int

const (
	Codex UsageProvider = iota
	OpenAI
	Claude
	Cursor
	Gemini
	DeepSeek
	OpenRouter
	ElevenLabs
	Groq
	Bedrock
	Unknown
	Antigravity
	Grok
	Copilot
)

func ProviderToString(p UsageProvider) string {
	switch p {
	case Codex:
		return "codex"
	case OpenAI:
		return "openai"
	case Claude:
		return "claude"
	case Cursor:
		return "cursor"
	case Gemini:
		return "gemini"
	case DeepSeek:
		return "deepseek"
	case OpenRouter:
		return "openrouter"
	case ElevenLabs:
		return "elevenlabs"
	case Groq:
		return "groq"
	case Bedrock:
		return "bedrock"
	case Antigravity:
		return "antigravity"
	case Grok:
		return "grok"
	case Copilot:
		return "copilot"
	default:
		return "unknown"
	}
}

func ProviderFromString(s string) UsageProvider {
	switch s {
	case "codex":
		return Codex
	case "openai":
		return OpenAI
	case "claude":
		return Claude
	case "cursor":
		return Cursor
	case "gemini":
		return Gemini
	case "deepseek":
		return DeepSeek
	case "openrouter":
		return OpenRouter
	case "elevenlabs":
		return ElevenLabs
	case "groq":
		return Groq
	case "bedrock":
		return Bedrock
	case "antigravity":
		return Antigravity
	case "grok":
		return Grok
	case "copilot":
		return Copilot
	default:
		return Unknown
	}
}

func GetDisplayName(p UsageProvider) string {
	switch p {
	case Codex:
		return "Codex"
	case OpenAI:
		return "OpenAI"
	case Claude:
		return "Claude"
	case Cursor:
		return "Cursor"
	case Gemini:
		return "Gemini"
	case DeepSeek:
		return "DeepSeek"
	case OpenRouter:
		return "OpenRouter"
	case ElevenLabs:
		return "ElevenLabs"
	case Groq:
		return "Groq"
	case Bedrock:
		return "AWS Bedrock"
	case Antigravity:
		return "Antigravity"
	case Grok:
		return "Grok"
	case Copilot:
		return "GitHub Copilot"
	default:
		return "Unknown"
	}
}

type ProviderConfig struct {
	ID           string `json:"id"`
	Enabled      *bool  `json:"enabled"`
	APIKey       string `json:"apiKey"`
	CookieHeader string `json:"cookieHeader"`
	Region       string `json:"region"`
}

func (p ProviderConfig) IsEnabled() bool {
	if p.Enabled == nil {
		return false
	}
	return *p.Enabled
}

type LimitsConfig struct {
	Version   int              `json:"version"`
	Providers []ProviderConfig `json:"providers"`
}

type UsageWindow struct {
	Label               string  `json:"Label"`
	UsedPercent         float64 `json:"UsedPercent"`
	ResetCountdown      string  `json:"ResetCountdown"`
	WindowSeconds       int     `json:"WindowSeconds"`
	PercentTextOverride *string `json:"PercentTextOverride"`
}

type ProviderUsage struct {
	Provider     string        `json:"Provider"`
	ID           string        `json:"Id"`
	DisplayName  string        `json:"DisplayName"`
	Windows      []UsageWindow `json:"Windows"`
	Status       string        `json:"Status"`
	IsMock       bool          `json:"IsMock"`
	HasError     bool          `json:"HasError"`
	ErrorMessage string        `json:"ErrorMessage"`
	Footer       string        `json:"Footer"`
}
