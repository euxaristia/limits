package terminal

import (
	"fmt"
	"math"
	"os"
	"strings"
)

var DisableColor = false

func UseColor() bool {
	if DisableColor {
		return false
	}
	if os.Getenv("FORCE_COLOR") == "1" {
		return true
	}
	// Check if stdout is redirected or NO_COLOR set
	if os.Getenv("NO_COLOR") != "" {
		return false
	}
	fileInfo, err := os.Stdout.Stat()
	if err != nil {
		return false
	}
	return (fileInfo.Mode() & os.ModeCharDevice) != 0
}

func Cyan(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[36m%s\u001b[0m", s)
	}
	return s
}

func Green(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[32m%s\u001b[0m", s)
	}
	return s
}

func Yellow(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[33m%s\u001b[0m", s)
	}
	return s
}

func Red(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[31m%s\u001b[0m", s)
	}
	return s
}

func Dim(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[2m%s\u001b[0m", s)
	}
	return s
}

func Bold(s string) string {
	if UseColor() {
		return fmt.Sprintf("\u001b[1m%s\u001b[0m", s)
	}
	return s
}

func RenderProgressBar(pct float64, width int) string {
	clamped := math.Max(0.0, math.Min(100.0, pct))
	filledCount := int(math.Round((clamped / 100.0) * float64(width)))
	emptyCount := width - filledCount
	if emptyCount < 0 {
		emptyCount = 0
	}
	filledStr := strings.Repeat("█", filledCount)
	emptyStr := strings.Repeat("░", emptyCount)
	barStr := fmt.Sprintf("[%s%s]", filledStr, emptyStr)

	if pct > 90.0 {
		return Red(barStr)
	} else if pct > 75.0 {
		return Yellow(barStr)
	}
	return Green(barStr)
}
