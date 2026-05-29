package runtime

import (
	"fmt"
	"os"
	"strings"
	"time"
)

// IsUpToDate is the Go equivalent of `TargetIncrementality.IsUpToDate`.
// Returns true iff every output exists and the oldest output is at least as
// new as the newest input. Empty inputs or outputs always return false (the
// target must execute), matching the C# behavior.
func IsUpToDate(targetName, inputsExpanded, outputsExpanded string) bool {
	inputs := toFileList(inputsExpanded)
	outputs := toFileList(outputsExpanded)
	if len(outputs) == 0 {
		return incrTrace(targetName, false, "no outputs", "", time.Time{}, "", time.Time{})
	}
	if len(inputs) == 0 {
		return incrTrace(targetName, false, "no inputs", "", time.Time{}, "", time.Time{})
	}
	var newestInput time.Time
	var newestInputPath string
	for _, in := range inputs {
		fi, err := os.Stat(in)
		if err != nil {
			continue
		}
		mt := fi.ModTime()
		if mt.After(newestInput) {
			newestInput = mt
			newestInputPath = in
		}
	}
	oldestOutput := time.Unix(1<<62, 0)
	var oldestOutputPath string
	for _, out := range outputs {
		fi, err := os.Stat(out)
		if err != nil {
			return incrTrace(targetName, false, "missing output", newestInputPath, newestInput, out, time.Time{})
		}
		mt := fi.ModTime()
		if mt.Before(oldestOutput) {
			oldestOutput = mt
			oldestOutputPath = out
		}
	}
	upToDate := !oldestOutput.Before(newestInput)
	reason := "up-to-date"
	if !upToDate {
		reason = "newer input"
	}
	return incrTrace(targetName, upToDate, reason, newestInputPath, newestInput, oldestOutputPath, oldestOutput)
}

func toFileList(s string) []string {
	if s == "" {
		return nil
	}
	parts := strings.FieldsFunc(s, func(r rune) bool {
		return r == ';' || r == '\n' || r == '\r'
	})
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

func incrTrace(targetName string, result bool, reason, inputPath string, inputTime time.Time, outputPath string, outputTime time.Time) bool {
	filter := os.Getenv("BSHARP_INCREMENTALITY_TRACE")
	if filter != "" && (filter == "1" || strings.Contains(strings.ToLower(targetName), strings.ToLower(filter))) {
		state := "dirty"
		if result {
			state = "up-to-date"
		}
		fmt.Fprintf(os.Stderr, "bsharp incrementality: %s: %s (%s); newest input=%s %s; oldest output=%s %s\n",
			targetName, state, reason, inputTime.UTC().Format(time.RFC3339Nano), inputPath, outputTime.UTC().Format(time.RFC3339Nano), outputPath)
	}
	return result
}
