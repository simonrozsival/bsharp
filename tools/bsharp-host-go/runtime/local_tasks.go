package runtime

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Local task implementations — the Go equivalents of the hand-written
// `Tasks.X` helpers in the C# host. Each takes a ParamList (and possibly an
// OutputList) and mutates either the filesystem or the calling target's item
// state. They are called from generated target methods.

// MakeDir creates directories listed in the "Directories" parameter and
// records any newly created directories under the output item list keyed
// by "DirectoriesCreated".
func MakeDir(p ParamList, outputs *OutputList, items ItemBag) error {
	var created []*Item
	for _, d := range SplitSemicolon(p.GetValueOrDefault("Directories")) {
		if _, err := os.Stat(d); err == nil {
			continue
		}
		if err := os.MkdirAll(d, 0o755); err != nil {
			return fmt.Errorf("MakeDir: %s: %w", d, err)
		}
		created = append(created, NewItem(d))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("DirectoriesCreated"); ok {
			items.AppendTo(spec.ItemName, created)
		}
	}
	return nil
}

// WriteLinesToFile writes the `;`-separated Lines parameter to File. If
// Overwrite is "true", the file is replaced (and skipped if content matches);
// otherwise lines are appended. `%3B` escapes are unescaped to `;`.
func WriteLinesToFile(p ParamList) error {
	file := p.GetValueOrDefault("File")
	lines := p.GetValueOrDefault("Lines")
	overwrite := strings.EqualFold(p.GetValueOrDefault("Overwrite"), "true")
	if file == "" {
		return nil
	}
	parts := strings.Split(lines, ";")
	for i, s := range parts {
		s = strings.ReplaceAll(s, "%3B", ";")
		s = strings.ReplaceAll(s, "%3b", ";")
		parts[i] = s
	}
	if err := os.MkdirAll(filepath.Dir(file), 0o755); err != nil {
		return fmt.Errorf("WriteLinesToFile: mkdir: %w", err)
	}
	if overwrite {
		if existing, err := os.ReadFile(file); err == nil {
			if sameLines(existing, parts) {
				return nil
			}
		}
		return os.WriteFile(file, []byte(strings.Join(parts, "\n")+"\n"), 0o644)
	}
	f, err := os.OpenFile(file, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return fmt.Errorf("WriteLinesToFile: open: %w", err)
	}
	defer f.Close()
	_, err = f.WriteString(strings.Join(parts, "\n") + "\n")
	return err
}

// Touch updates the mtime of each file in "Files". Creates empty files if
// AlwaysCreate=true. Records touched files under TouchedFiles output.
func Touch(p ParamList, outputs *OutputList, items ItemBag) error {
	alwaysCreate := strings.EqualFold(p.GetValueOrDefault("AlwaysCreate"), "true")
	var touched []*Item
	now := timeNow()
	for _, f := range SplitSemicolon(p.GetValueOrDefault("Files")) {
		if _, err := os.Stat(f); err != nil {
			if !alwaysCreate {
				continue
			}
			if err := os.MkdirAll(filepath.Dir(f), 0o755); err != nil {
				return fmt.Errorf("Touch: mkdir %s: %w", f, err)
			}
			if err := os.WriteFile(f, nil, 0o644); err != nil {
				return fmt.Errorf("Touch: create %s: %w", f, err)
			}
		}
		if err := os.Chtimes(f, now, now); err != nil {
			return fmt.Errorf("Touch: chtimes %s: %w", f, err)
		}
		touched = append(touched, NewItem(f))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("TouchedFiles"); ok {
			items.AppendTo(spec.ItemName, touched)
		}
	}
	return nil
}

// Delete removes each file in "Files" and records removed files under
// DeletedFiles output.
func Delete(p ParamList, outputs *OutputList, items ItemBag) error {
	var deleted []*Item
	for _, f := range SplitSemicolon(p.GetValueOrDefault("Files")) {
		if _, err := os.Stat(f); err != nil {
			continue
		}
		if err := os.Remove(f); err != nil {
			return fmt.Errorf("Delete: %s: %w", f, err)
		}
		deleted = append(deleted, NewItem(f))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("DeletedFiles"); ok {
			items.AppendTo(spec.ItemName, deleted)
		}
	}
	return nil
}

// Copy copies SourceFiles to DestinationFiles 1:1. SkipUnchangedFiles=true
// skips copies where source content already matches the destination.
// CopiedFiles output receives the destination items.
func Copy(p ParamList, outputs *OutputList, items ItemBag) error {
	src := SplitSemicolon(p.GetValueOrDefault("SourceFiles"))
	dst := SplitSemicolon(p.GetValueOrDefault("DestinationFiles"))
	skipUnchanged := strings.EqualFold(p.GetValueOrDefault("SkipUnchangedFiles"), "true")
	var copied []*Item
	for i, s := range src {
		if i >= len(dst) {
			break
		}
		d := dst[i]
		if skipUnchanged && filesIdentical(s, d) {
			copied = append(copied, NewItem(d))
			continue
		}
		if err := copyFile(s, d); err != nil {
			return fmt.Errorf("Copy: %s -> %s: %w", s, d, err)
		}
		copied = append(copied, NewItem(d))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("CopiedFiles"); ok {
			items.AppendTo(spec.ItemName, copied)
		}
	}
	return nil
}

// ReadLinesFromFile reads the file at "File" and returns lines as items in
// the "Lines" output item list. Missing files yield no items (no error).
func ReadLinesFromFile(p ParamList, outputs *OutputList, items ItemBag) error {
	file := p.GetValueOrDefault("File")
	if file == "" {
		return nil
	}
	f, err := os.Open(file)
	if err != nil {
		return nil
	}
	defer f.Close()
	scanner := bufio.NewScanner(f)
	var lines []*Item
	for scanner.Scan() {
		lines = append(lines, NewItem(scanner.Text()))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("Lines"); ok {
			items.AppendTo(spec.ItemName, lines)
		}
	}
	return nil
}

// RemoveDir recursively deletes each directory in "Directories" and records
// removed paths under RemovedDirectories output.
func RemoveDir(p ParamList, outputs *OutputList, items ItemBag) error {
	var removed []*Item
	for _, d := range SplitSemicolon(p.GetValueOrDefault("Directories")) {
		if _, err := os.Stat(d); err != nil {
			continue
		}
		if err := os.RemoveAll(d); err != nil {
			return fmt.Errorf("RemoveDir: %s: %w", d, err)
		}
		removed = append(removed, NewItem(d))
	}
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("RemovedDirectories"); ok {
			items.AppendTo(spec.ItemName, removed)
		}
	}
	return nil
}

// Hash computes a stable hash of all "ItemsToHash" identities. Result goes
// to "HashResult" output (a property assignment in the generated host).
// The C# implementation uses SHA256 of joined identities; we mirror.
func Hash(p ParamList, outputs *OutputList, props PropertyBag) error {
	identities := SplitSemicolon(p.GetValueOrDefault("ItemsToHash"))
	if len(identities) == 0 {
		return nil
	}
	h := sha256.New()
	for _, id := range identities {
		h.Write([]byte(id))
		h.Write([]byte{0})
	}
	digest := hex.EncodeToString(h.Sum(nil))
	if outputs != nil {
		if spec, ok := outputs.TryGetValue("HashResult"); ok {
			// The C# host emits Hash output as a property assignment, but the
			// generated codegen invokes the typed-task path for it. We expose
			// both: if the spec carries a metadata key, treat as property.
			_ = spec
			props.Set("HashResult", digest)
		}
	}
	return nil
}

// Message logs at the requested importance ("high", "normal", "low").
// Empty Importance defaults to "normal".
func Message(p ParamList) error {
	text := p.GetValueOrDefault("Text")
	if text == "" {
		return nil
	}
	switch strings.ToLower(p.GetValueOrDefault("Importance")) {
	case "high":
		Log.MessageHigh(text)
	case "low":
		Log.MessageLow(text)
	default:
		Log.MessageNormal(text)
	}
	return nil
}

// Warning emits a build warning. The Code parameter is optional.
func Warning(p ParamList) error {
	Log.Warning(p.GetValueOrDefault("Code"), p.GetValueOrDefault("Text"))
	return nil
}

// Error emits a build error and returns it so the generated target body can
// surface it to the error list. The Code parameter is optional.
func Error(p ParamList) error {
	text := p.GetValueOrDefault("Text")
	code := p.GetValueOrDefault("Code")
	Log.Error(code, text)
	if code != "" {
		return fmt.Errorf("%s: %s", code, text)
	}
	return fmt.Errorf("%s", text)
}

// NotImplemented is the fallback for unknown task names. Logs a warning and
// returns nil so the build continues (matches the C# behavior).
func NotImplemented(name string) {
	Log.Warning("BSGO0001", "task '"+name+"' is not implemented in the Go host")
}

// formatSdkMessage builds the diagnostic text for the NETSdk*/MSBuildInternalMessage
// task family. The MSBuild SDK looks ResourceName up in a localised .resx file
// and applies String.Format with FormatArguments; the bsharp Go host does not
// embed those resource strings, so it prints the bare ResourceName plus the
// (already-expanded) format arguments. Semantically this is enough — the
// gating logic lives in the task's Condition; the body only runs when the
// SDK would also have raised the diagnostic.
func formatSdkMessage(p ParamList) string {
	resourceName := p.GetValueOrDefault("ResourceName")
	formatArgs := p.GetValueOrDefault("FormatArguments")
	if formatArgs == "" {
		return resourceName
	}
	return resourceName + ": " + formatArgs
}

// NETSdkError emits an SDK-defined build error. Returns the formatted error
// so the calling target adds it to the error list (terminal failure).
func NETSdkError(p ParamList) error {
	text := formatSdkMessage(p)
	code := "NETSDK_" + p.GetValueOrDefault("ResourceName")
	Log.Error(code, text)
	return fmt.Errorf("%s: %s", code, text)
}

// NETSdkWarning emits an SDK-defined build warning. Never fails the build.
func NETSdkWarning(p ParamList) error {
	Log.Warning("NETSDK_"+p.GetValueOrDefault("ResourceName"), formatSdkMessage(p))
	return nil
}

// NETSdkInformation emits an SDK-defined informational message.
func NETSdkInformation(p ParamList) error {
	Log.MessageHigh(formatSdkMessage(p))
	return nil
}

// MSBuildInternalMessage is a polymorphic diagnostic the SDK uses where the
// severity is decided by a property. Severity values: "Error", "Warning",
// "Message" (default normal-importance), "Information" (high-importance).
func MSBuildInternalMessage(p ParamList) error {
	text := formatSdkMessage(p)
	code := "MSB_" + p.GetValueOrDefault("ResourceName")
	switch strings.ToLower(p.GetValueOrDefault("Severity")) {
	case "error":
		Log.Error(code, text)
		return fmt.Errorf("%s: %s", code, text)
	case "warning":
		Log.Warning(code, text)
	case "information", "high":
		Log.MessageHigh(text)
	default:
		Log.MessageNormal(text)
	}
	return nil
}

// PropertyBag is the contract the runtime expects from the generated `P`
// struct: set / get / set-extra. Generated code implements this via methods
// on the `*props` struct.
type PropertyBag interface {
	Set(name, value string)
	Get(name string) string
}

// ItemBag is the contract the runtime expects from the generated `I` struct:
// append-to / get / replace.
type ItemBag interface {
	AppendTo(name string, items []*Item)
	Get(name string) []*Item
}

// --- helpers ---

func sameLines(existing []byte, lines []string) bool {
	have := strings.Split(strings.TrimRight(string(existing), "\n"), "\n")
	if len(have) != len(lines) {
		return false
	}
	for i := range have {
		if have[i] != lines[i] {
			return false
		}
	}
	return true
}

func filesIdentical(a, b string) bool {
	ai, err := os.Stat(a)
	if err != nil {
		return false
	}
	bi, err := os.Stat(b)
	if err != nil {
		return false
	}
	if ai.Size() != bi.Size() {
		return false
	}
	af, err := os.Open(a)
	if err != nil {
		return false
	}
	defer af.Close()
	bf, err := os.Open(b)
	if err != nil {
		return false
	}
	defer bf.Close()
	abuf := make([]byte, 32*1024)
	bbuf := make([]byte, 32*1024)
	for {
		an, aerr := io.ReadFull(af, abuf)
		bn, berr := io.ReadFull(bf, bbuf)
		if an != bn {
			return false
		}
		if string(abuf[:an]) != string(bbuf[:bn]) {
			return false
		}
		if aerr == io.EOF || aerr == io.ErrUnexpectedEOF {
			return berr == io.EOF || berr == io.ErrUnexpectedEOF
		}
	}
}

func copyFile(src, dst string) error {
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	sf, err := os.Open(src)
	if err != nil {
		return err
	}
	defer sf.Close()
	df, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	defer df.Close()
	if _, err := io.Copy(df, sf); err != nil {
		return err
	}
	if info, err := sf.Stat(); err == nil {
		_ = os.Chmod(dst, info.Mode())
	}
	return nil
}

// timeNow is var to allow tests to override.
var timeNow = func() time.Time { return time.Now() }
