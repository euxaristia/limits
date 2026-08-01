package parsers

import (
	"fmt"
	"regexp"
	"strings"
)

var emailRegex = regexp.MustCompile(`\b([a-zA-Z0-9._%+-]+)@([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b`)

func RedactEmail(input string) string {
	if strings.TrimSpace(input) == "" {
		return ""
	}
	return emailRegex.ReplaceAllStringFunc(input, func(m string) string {
		parts := strings.Split(m, "@")
		if len(parts) != 2 {
			return m
		}
		local := parts[0]
		domain := parts[1]

		var redactedLocal string
		if len(local) <= 2 {
			redactedLocal = fmt.Sprintf("%c***", local[0])
		} else {
			redactedLocal = fmt.Sprintf("%c***%c", local[0], local[len(local)-1])
		}
		return fmt.Sprintf("%s@%s", redactedLocal, domain)
	})
}
