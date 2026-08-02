package parsers

import (
	"testing"
	"time"
)

func TestParseCodexUsage(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	usage, err := ParseCodexUsage([]byte(`{
        "plan_type":"plus",
        "rate_limit":{
            "primary_window":{"used_percent":42,"limit_window_seconds":18000,"reset_at":1700003600},
            "secondary_window":{"used_percent":5,"limit_window_seconds":604800,"reset_after_seconds":7200}
        }
    }`), now)
	if err != nil {
		t.Fatal(err)
	}
	if usage.PlanType != "plus" || len(usage.Windows) != 2 {
		t.Fatalf("unexpected usage: %#v", usage)
	}
	if got := usage.Windows[0]; got.Label != "Primary" || got.UsedPercent != 42 || got.WindowSeconds != 18000 || got.ResetCountdown != "1h 0m" {
		t.Fatalf("unexpected primary window: %#v", got)
	}
	if got := usage.Windows[1]; got.Label != "Secondary" || got.ResetCountdown != "2h 0m" {
		t.Fatalf("unexpected secondary window: %#v", got)
	}
}

func TestParseCodexUsageRejectsMissingWindows(t *testing.T) {
	_, err := ParseCodexUsage([]byte(`{"rate_limit":{}}`), time.Now())
	if err == nil {
		t.Fatal("expected missing windows error")
	}
}
