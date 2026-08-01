package fetchers

import "testing"

func labelsOf(specs []windowSpec) []string {
	out := make([]string, len(specs))
	for i, s := range specs {
		out[i] = s.label
	}
	return out
}

func overrideOf(t *testing.T, specs []windowSpec, label string) string {
	t.Helper()
	for _, s := range specs {
		if s.label == label {
			if s.pctTextOverride == nil {
				t.Fatalf("window %q has no percent text", label)
			}
			return *s.pctTextOverride
		}
	}
	t.Fatalf("no window labelled %q in %v", label, labelsOf(specs))
	return ""
}

func wantLabels(t *testing.T, specs []windowSpec, want ...string) {
	t.Helper()
	got := labelsOf(specs)
	if len(got) != len(want) {
		t.Fatalf("got windows %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got windows %v, want %v", got, want)
		}
	}
}

func count(v float64) *float64 { return &v }

// Mirrors the shape GitHub returns for a Copilot Free account: premium
// interactions are listed but not entitled.
func freeAccount() copilotUser {
	return copilotUser{
		Login:         "octocat",
		AccessTypeSku: "free_limited_copilot",
		CopilotPlan:   "individual",
		Quotas: map[string]copilotQuota{
			"chat":                 {HasQuota: true, PercentRemaining: 98.0, Entitlement: 200, Remaining: count(196)},
			"completions":          {HasQuota: true, PercentRemaining: 100.0, Entitlement: 2000, Remaining: count(2000)},
			"premium_interactions": {HasQuota: false, PercentRemaining: 0.0, Entitlement: 0, Remaining: count(0)},
		},
	}
}

func TestCopilotWindowsSkipsUnentitledQuotas(t *testing.T) {
	specs, exhausted := copilotWindows(freeAccount())

	wantLabels(t, specs, "Chat", "Completions")
	if exhausted {
		t.Error("account with quota left reported as exhausted")
	}
}

func TestCopilotWindowsReportsUsedNotRemaining(t *testing.T) {
	specs, _ := copilotWindows(freeAccount())

	if got, want := overrideOf(t, specs, "Chat"), "4 / 200 (2.0% used)"; got != want {
		t.Errorf("chat text = %q, want %q", got, want)
	}
	for _, s := range specs {
		if s.label == "Chat" && s.pct != 2.0 {
			t.Errorf("chat percent = %v, want 2", s.pct)
		}
	}
}

func TestCopilotWindowsFallsBackToPercentWhenCountMissing(t *testing.T) {
	specs, _ := copilotWindows(copilotUser{Quotas: map[string]copilotQuota{
		"chat": {HasQuota: true, PercentRemaining: 75.0, Entitlement: 200},
	}})

	if got, want := overrideOf(t, specs, "Chat"), "50 / 200 (25.0% used)"; got != want {
		t.Errorf("chat text = %q, want %q", got, want)
	}
}

func TestCopilotWindowsFlagsExhaustedQuota(t *testing.T) {
	specs, exhausted := copilotWindows(copilotUser{Quotas: map[string]copilotQuota{
		"chat": {HasQuota: true, PercentRemaining: 0.0, Entitlement: 200, Remaining: count(0)},
	}})

	if !exhausted {
		t.Error("spent quota not flagged as exhausted")
	}
	if got, want := overrideOf(t, specs, "Chat"), "200 / 200 (100.0% used)"; got != want {
		t.Errorf("chat text = %q, want %q", got, want)
	}
}

func TestCopilotWindowsShowsUnlimitedWithoutABar(t *testing.T) {
	specs, exhausted := copilotWindows(copilotUser{Quotas: map[string]copilotQuota{
		"completions": {Unlimited: true, HasQuota: false},
	}})

	if got, want := overrideOf(t, specs, "Completions"), "Unlimited"; got != want {
		t.Errorf("completions text = %q, want %q", got, want)
	}
	if specs[0].pct != 0.0 {
		t.Errorf("unlimited quota shown at %v%%, want 0", specs[0].pct)
	}
	if exhausted {
		t.Error("unlimited quota reported as exhausted")
	}
}

// A quota GitHub adds later should still appear, after the ones we know.
func TestCopilotWindowsKeepsUnknownQuotas(t *testing.T) {
	specs, _ := copilotWindows(copilotUser{Quotas: map[string]copilotQuota{
		"chat":       {HasQuota: true, PercentRemaining: 100.0, Entitlement: 200, Remaining: count(200)},
		"agent_mode": {HasQuota: true, PercentRemaining: 50.0, Entitlement: 10, Remaining: count(5)},
	}})

	wantLabels(t, specs, "Chat", "Agent mode")
}

func TestCopilotWindowsIgnoresEmptyPayload(t *testing.T) {
	specs, exhausted := copilotWindows(copilotUser{})

	if len(specs) != 0 {
		t.Errorf("got windows %v, want none", labelsOf(specs))
	}
	if exhausted {
		t.Error("empty payload reported as exhausted")
	}
}
