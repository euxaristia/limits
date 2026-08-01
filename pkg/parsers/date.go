package parsers

import (
	"fmt"
	"time"
)

func FormatCountdown(resetsAtStr string) string {
	t, err := time.Parse(time.RFC3339, resetsAtStr)
	if err != nil {
		// Try ISO8601 variations
		t, err = time.Parse("2006-01-02T15:04:05Z07:00", resetsAtStr)
	}
	if err != nil {
		t, err = time.Parse("2006-01-02T15:04:05", resetsAtStr)
	}
	if err != nil {
		return "Unknown"
	}

	diff := t.UTC().Sub(time.Now().UTC())
	if diff.Seconds() <= 0 {
		return "Resets now"
	}

	hours := int(diff.Hours())
	minutes := int(diff.Minutes()) % 60

	if hours > 24 {
		return fmt.Sprintf("%dd %dh", hours/24, hours%24)
	} else if hours > 0 {
		return fmt.Sprintf("%dh %dm", hours, minutes)
	}
	return fmt.Sprintf("%dm", minutes)
}
