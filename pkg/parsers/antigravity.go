package parsers

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strings"
	"time"
)

type AntigravityBucket struct {
	GroupLabel       string
	Members          string
	UsedPercent      float64
	RemainingPercent float64
	ResetCountdown   string
	Timeframe        string
}

type rawBucket struct {
	ModelId           string
	Family            string
	RemainingFraction float64
	ResetCountdown    string
	Timeframe         string
}

func familyFromModelID(modelID string) string {
	m := strings.ToLower(modelID)
	if strings.HasPrefix(m, "gemini-") || strings.Contains(m, "gemini") {
		return "Gemini"
	} else if strings.HasPrefix(m, "claude-") || strings.Contains(m, "claude") {
		return "Claude & GPT"
	} else if strings.HasPrefix(m, "gpt-") || strings.HasPrefix(m, "gpt_") {
		return "Claude & GPT"
	} else if strings.HasPrefix(m, "chat_") || strings.HasPrefix(m, "tab_") {
		return "Internal"
	}
	return "Antigravity"
}

func deriveTimeframe(rawIso string, countdown string) string {
	if rawIso == "" {
		return "Quota"
	}
	t, err := time.Parse(time.RFC3339, rawIso)
	if err == nil {
		diff := t.UTC().Sub(time.Now().UTC())
		if diff.Hours() <= 8.0 {
			return "Session"
		}
		return "Weekly"
	}
	if strings.HasSuffix(countdown, "h") || (strings.Contains(countdown, "h ") && !strings.Contains(countdown, "d")) {
		return "Session"
	} else if strings.Contains(countdown, "d") {
		return "Weekly"
	}
	return "Quota"
}

func CanonicalModelName(modelID string) string {
	m := strings.ToLower(modelID)
	if strings.Contains(m, "flash-lite") || strings.Contains(m, "flash_lite") || strings.Contains(m, "flashlite") {
		return "Gemini Flash-Lite"
	} else if strings.Contains(m, "flash") {
		return "Gemini Flash"
	} else if strings.Contains(m, "pro") || strings.Contains(m, "gemini") {
		return "Gemini Pro"
	} else if strings.Contains(m, "sonnet") {
		return "Claude Sonnet"
	} else if strings.Contains(m, "opus") {
		return "Claude Opus"
	} else if strings.Contains(m, "gpt") {
		return "GPT"
	}
	return modelID
}

func ParseAntigravityQuota(data []byte) []AntigravityBucket {
	var root map[string]interface{}
	if err := json.Unmarshal(data, &root); err != nil {
		return nil
	}

	familyRank := func(label string) int {
		if strings.EqualFold(label, "Gemini") {
			return 0
		} else if strings.HasPrefix(strings.ToLower(label), "claude") {
			return 1
		}
		return 2
	}

	if groupsRaw, ok := root["groups"].([]interface{}); ok && len(groupsRaw) > 0 {
		var result []AntigravityBucket
		for _, g := range groupsRaw {
			groupMap, ok := g.(map[string]interface{})
			if !ok {
				continue
			}
			groupName, _ := groupMap["displayName"].(string)
			familyLabel := "Antigravity"
			if strings.HasPrefix(strings.ToLower(groupName), "gemini") {
				familyLabel = "Gemini"
			} else if strings.Contains(strings.ToLower(groupName), "claude") || strings.Contains(strings.ToLower(groupName), "gpt") {
				familyLabel = "Claude & GPT"
			}

			memberDesc, _ := groupMap["description"].(string)
			memberList := memberDesc
			if familyLabel == "Gemini" {
				memberList = "Gemini 3.6 Flash, Gemini 3.5 Flash, Gemini 3.1 Pro"
			} else if familyLabel == "Claude & GPT" {
				memberList = "Claude Sonnet 4.6, Claude Opus 4.6, GPT-OSS 120B"
			} else if idx := strings.Index(memberDesc, ":"); idx != -1 {
				memberList = strings.TrimSpace(memberDesc[idx+1:])
			}

			if bucketsRaw, ok := groupMap["buckets"].([]interface{}); ok {
				for _, b := range bucketsRaw {
					bMap, ok := b.(map[string]interface{})
					if !ok {
						continue
					}
					if disabled, ok := bMap["disabled"].(bool); ok && disabled {
						continue
					}
					var remaining float64
					if remNum, ok := bMap["remainingFraction"].(float64); ok {
						remaining = remNum
					}
					resetRaw, _ := bMap["resetTime"].(string)
					reset := "Never Resets"
					if resetRaw != "" {
						reset = FormatCountdown(resetRaw)
					}
					windowStr, _ := bMap["window"].(string)
					timeframe := deriveTimeframe(resetRaw, reset)
					if strings.EqualFold(windowStr, "weekly") {
						timeframe = "Weekly"
					} else if strings.EqualFold(windowStr, "5h") {
						timeframe = "Session"
					}

					used := math.Max(0.0, math.Min(100.0, (1.0-remaining)*100.0))
					remPct := math.Max(0.0, math.Min(100.0, remaining*100.0))

					result = append(result, AntigravityBucket{
						GroupLabel:       familyLabel,
						Members:          memberList,
						UsedPercent:      used,
						RemainingPercent: remPct,
						ResetCountdown:   reset,
						Timeframe:        timeframe,
					})
				}
			}
		}

		sort.Slice(result, func(i, j int) bool {
			rI, rJ := familyRank(result[i].GroupLabel), familyRank(result[j].GroupLabel)
			if rI != rJ {
				return rI < rJ
			}
			// Session is ranked before Weekly: if the session window is exhausted,
			// the weekly one is moot until it resets, so it's the more urgent number.
			tI := 1
			if result[i].Timeframe == "Session" {
				tI = 0
			}
			tJ := 1
			if result[j].Timeframe == "Session" {
				tJ = 0
			}
			if tI != tJ {
				return tI < tJ
			}
			return result[i].ResetCountdown < result[j].ResetCountdown
		})
		return result
	}

	// Fallback to "buckets" array
	if bucketsRaw, ok := root["buckets"].([]interface{}); ok {
		var raws []rawBucket
		for _, b := range bucketsRaw {
			bMap, ok := b.(map[string]interface{})
			if !ok {
				continue
			}
			modelID, _ := bMap["modelId"].(string)
			if modelID == "" {
				continue
			}
			mLower := strings.ToLower(modelID)
			if strings.HasPrefix(mLower, "chat_") || strings.HasPrefix(mLower, "tab_") {
				continue
			}
			var remaining float64
			if remNum, ok := bMap["remainingFraction"].(float64); ok {
				remaining = remNum
			}
			resetRaw, _ := bMap["resetTime"].(string)
			reset := "Never Resets"
			if resetRaw != "" {
				reset = FormatCountdown(resetRaw)
			}
			timeframe := deriveTimeframe(resetRaw, reset)
			raws = append(raws, rawBucket{
				ModelId:           modelID,
				Family:            familyFromModelID(modelID),
				RemainingFraction: remaining,
				ResetCountdown:    reset,
				Timeframe:         timeframe,
			})
		}

		groups := make(map[string][]rawBucket)
		for _, r := range raws {
			k := fmt.Sprintf("%s|%s|%s", r.Family, r.Timeframe, r.ResetCountdown)
			groups[k] = append(groups[k], r)
		}

		var result []AntigravityBucket
		for _, entries := range groups {
			if len(entries) == 0 {
				continue
			}
			first := entries[0]
			minRemaining := 1.0
			var memberNames []string
			seenMembers := make(map[string]bool)

			for _, e := range entries {
				if e.RemainingFraction < minRemaining {
					minRemaining = e.RemainingFraction
				}
				name := CanonicalModelName(e.ModelId)
				if !seenMembers[name] {
					seenMembers[name] = true
					memberNames = append(memberNames, name)
				}
			}
			sort.Strings(memberNames)
			used := math.Max(0.0, math.Min(100.0, (1.0-minRemaining)*100.0))
			remPct := math.Max(0.0, math.Min(100.0, minRemaining*100.0))

			result = append(result, AntigravityBucket{
				GroupLabel:       first.Family,
				Members:          strings.Join(memberNames, ", "),
				UsedPercent:      used,
				RemainingPercent: remPct,
				ResetCountdown:   first.ResetCountdown,
				Timeframe:        first.Timeframe,
			})
		}

		sort.Slice(result, func(i, j int) bool {
			rI, rJ := familyRank(result[i].GroupLabel), familyRank(result[j].GroupLabel)
			if rI != rJ {
				return rI < rJ
			}
			tI := 1
			if result[i].Timeframe == "Session" {
				tI = 0
			}
			tJ := 1
			if result[j].Timeframe == "Session" {
				tJ = 0
			}
			if tI != tJ {
				return tI < tJ
			}
			return result[i].ResetCountdown < result[j].ResetCountdown
		})
		return result
	}

	return nil
}
