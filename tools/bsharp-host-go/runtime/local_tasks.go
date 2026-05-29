package runtime

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
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
func MakeDir(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeItemOutput(outputs, items, "DirectoriesCreated", created)
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
func Touch(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeItemOutput(outputs, items, "TouchedFiles", touched)
	return nil
}

// Delete removes each file in "Files" and records removed files under
// DeletedFiles output.
func Delete(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeItemOutput(outputs, items, "DeletedFiles", deleted)
	return nil
}

// Copy copies SourceFiles to DestinationFiles 1:1. SkipUnchangedFiles=true
// skips copies where source content already matches the destination.
// CopiedFiles output receives the destination items.
func Copy(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	src := SplitSemicolon(p.GetValueOrDefault("SourceFiles"))
	dst := SplitSemicolon(p.GetValueOrDefault("DestinationFiles"))
	if len(dst) == 0 {
		if folder := p.GetValueOrDefault("DestinationFolder"); folder != "" {
			dst = make([]string, 0, len(src))
			for _, s := range src {
				if s == "" {
					continue
				}
				dst = append(dst, filepath.Join(folder, filepath.Base(s)))
			}
		}
	}
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
	writeItemOutput(outputs, items, "CopiedFiles", copied)
	// MSBuild's Copy task lets <Output TaskParameter="DestinationFiles"/>
	// reflect back the destination identities (MSBuild treats input
	// parameters as bindable outputs). Mirror that so SDK patterns like
	// `<Output TaskParameter="DestinationFiles" ItemName="FileWrites"/>`
	// work — the runtime writes each binding independently and the
	// OutputList only commits the ones the caller asked for.
	writeItemOutput(outputs, items, "DestinationFiles", copied)
	return nil
}

// ReadLinesFromFile reads the file at "File" and returns lines as items in
// the "Lines" output item list. Missing files yield no items (no error).
func ReadLinesFromFile(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeItemOutput(outputs, items, "Lines", lines)
	return nil
}

// RemoveDir recursively deletes each directory in "Directories" and records
// removed paths under RemovedDirectories output.
func RemoveDir(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeItemOutput(outputs, items, "RemovedDirectories", removed)
	return nil
}

// Hash computes a stable hash of all "ItemsToHash" identities. Result goes
// to "HashResult" output (a property assignment in the generated host).
// The C# implementation uses SHA256 of joined identities; we mirror.
func Hash(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
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
	writeStringOutput(outputs, items, props, "HashResult", digest)
	return nil
}

// ConvertToAbsolutePath resolves the "Paths" parameter to absolute paths.
// Results are emitted under the "AbsolutePaths" output (either as items
// when ItemName is bound, or as a property when PropertyName is bound).
// Property bindings are joined with `;` to mirror MSBuild semantics for an
// ITaskItem[]-typed output assigned to a string property.
func ConvertToAbsolutePath(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	paths := SplitSemicolon(p.GetValueOrDefault("Paths"))
	resolved := make([]*Item, 0, len(paths))
	resolvedStrings := make([]string, 0, len(paths))
	for _, raw := range paths {
		if raw == "" {
			continue
		}
		abs, err := filepath.Abs(raw)
		if err != nil {
			return fmt.Errorf("ConvertToAbsolutePath: %s: %w", raw, err)
		}
		resolved = append(resolved, NewItem(abs))
		resolvedStrings = append(resolvedStrings, abs)
	}
	if outputs == nil {
		return nil
	}
	if spec, ok := outputs.TryGetValue("AbsolutePaths"); ok {
		if spec.PropertyName != "" {
			props.Set(spec.PropertyName, strings.Join(resolvedStrings, ";"))
		} else if spec.ItemName != "" {
			items.AppendTo(spec.ItemName, resolved)
		}
	}
	return nil
}

// RemoveDuplicates dedupes the "Inputs" item identities (case-insensitive)
// and emits the deduped list under the "Filtered" output. Preserves first-
// occurrence order. Also sets "HadAnyDuplicates" = "True"/"False" on the
// property output if that binding is present (MSBuild's RemoveDuplicates
// surfaces both Filtered + HadAnyDuplicates).
func RemoveDuplicates(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	seen := map[string]bool{}
	var filtered []*Item
	hadDup := false
	for _, id := range SplitSemicolon(p.GetValueOrDefault("Inputs")) {
		key := strings.ToLower(id)
		if seen[key] {
			hadDup = true
			continue
		}
		seen[key] = true
		filtered = append(filtered, NewItem(id))
	}
	writeItemOutput(outputs, items, "Filtered", filtered)
	writeStringOutput(outputs, items, props, "HadAnyDuplicates", boolStr(hadDup))
	return nil
}

// FindUnderPath partitions the "Files" identities into items rooted under
// "Path" (InPath) and everything else (OutOfPath). MSBuild's
// case-insensitive prefix match on resolved absolute paths is preserved
// so the Clean / IncrementalClean / publish file-write filters land in
// the same buckets they would under MSBuild itself.
//
// MSBuild also exposes UpdateToAbsolutePaths which, when "true", replaces
// matched identities with their canonical absolute form. We honor it for
// the InPath bucket only — OutOfPath always keeps the caller's input
// identity verbatim (same as MSBuild).
//
// Files arriving via ParamList have been collapsed to a `;`-joined
// identity string by the caller, so the task only sees raw paths and not
// the original ITaskItem metadata. The Clean targets that consume
// FindUnderPath only read Identity downstream, so the metadata loss is
// fine for the closed-world subset.
func FindUnderPath(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	root := strings.TrimSpace(p.GetValueOrDefault("Path"))
	if root == "" {
		// Path is [Required] on MSBuild — surface the same failure shape
		// here instead of silently treating every file as OutOfPath.
		return fmt.Errorf("FindUnderPath: Path parameter is required")
	}
	absRoot, err := filepath.Abs(root)
	if err != nil {
		return fmt.Errorf("FindUnderPath: invalid Path %q: %w", root, err)
	}
	absRoot = ensureTrailingSeparator(absRoot)
	rootLower := strings.ToLower(absRoot)
	update := strings.EqualFold(p.GetValueOrDefault("UpdateToAbsolutePaths"), "true")
	files := SplitSemicolon(p.GetValueOrDefault("Files"))
	var inPath, outOfPath []*Item
	for _, f := range files {
		if f == "" {
			continue
		}
		absF, err := filepath.Abs(f)
		if err != nil {
			return fmt.Errorf("FindUnderPath: invalid Files entry %q: %w", f, err)
		}
		if strings.HasPrefix(strings.ToLower(absF), rootLower) {
			id := f
			if update {
				id = absF
			}
			inPath = append(inPath, NewItem(id))
		} else {
			outOfPath = append(outOfPath, NewItem(f))
		}
	}
	writeItemOutput(outputs, items, "InPath", inPath)
	writeItemOutput(outputs, items, "OutOfPath", outOfPath)
	return nil
}

// JoinItemsArgs holds the typed inputs to JoinItems. Unlike most local
// tasks which receive everything through ParamList, JoinItems needs the
// full *Item slices for Left and Right so per-item metadata can be
// merged. The emitter looks up each side via I.Get(name) (for `@(name)`
// references) or builds bare identity-only items via ItemsFromIdentities
// (for `$(property)` references that expand to a `;`-separated list).
//
// Metadata allow-lists are passed verbatim ("", "*", or a `;`-separated
// list of metadata names). MSBuild splits string[] parameters on `;`, so
// `LeftMetadata="HintPath;Aliases"` arrives as a two-name allow-list.
type JoinItemsArgs struct {
	Left          []*Item
	Right         []*Item
	LeftKey       string
	RightKey      string
	LeftMetadata  string
	RightMetadata string
	ItemSpecToUse string
}

// JoinItems performs an inner join of Left × Right on (LeftKey, RightKey)
// where empty keys default to Identity. Each Left item that matches a
// right entry produces one output item; right duplicates after the first
// per key are ignored (mirrors Linq.Join semantics used by the SDK
// implementation). Comparison is case-insensitive. Metadata is merged
// per Left/RightMetadata ("*" = all, empty = none, otherwise the named
// keys). ItemSpecToUse picks Left/Right identity for the output;
// when empty, output identity defaults to the Left's join-key value
// (matching Microsoft.NET.Build.Tasks.JoinItems).
//
// Cross-checked against the decompiled SDK source: when both sides use
// "*" allow-lists, MSBuild copies left's metadata first, then right's
// metadata (right wins on collisions); OriginalItemSpec is stripped on
// any "*" copy.
func JoinItems(args JoinItemsArgs, outputs *OutputList, items ItemBag, props PropertyBag) error {
	leftMeta := parseJoinMetaList(args.LeftMetadata)
	rightMeta := parseJoinMetaList(args.RightMetadata)
	useAllLeftMeta := len(leftMeta) == 1 && leftMeta[0] == "*"
	useAllRightMeta := len(rightMeta) == 1 && rightMeta[0] == "*"

	useLeftItemSpec := strings.EqualFold(args.ItemSpecToUse, "Left")
	useRightItemSpec := strings.EqualFold(args.ItemSpecToUse, "Right")
	if args.ItemSpecToUse != "" && !useLeftItemSpec && !useRightItemSpec {
		return fmt.Errorf("JoinItems: ItemSpecToUse must be empty, 'Left', or 'Right' (got %q)", args.ItemSpecToUse)
	}

	rightIndex := make(map[string]*Item, len(args.Right))
	for _, r := range args.Right {
		k := strings.ToLower(joinItemKey(args.RightKey, r))
		if _, ok := rightIndex[k]; !ok {
			rightIndex[k] = r
		}
	}

	var result []*Item
	for _, l := range args.Left {
		k := strings.ToLower(joinItemKey(args.LeftKey, l))
		r, ok := rightIndex[k]
		if !ok {
			continue
		}
		result = append(result, mergeJoinedItem(
			l, r,
			args.LeftKey, args.RightKey,
			leftMeta, rightMeta,
			useAllLeftMeta, useAllRightMeta,
			useLeftItemSpec, useRightItemSpec,
		))
	}

	writeItemOutput(outputs, items, "JoinResult", result)
	return nil
}

// ItemsFromIdentities builds bare identity-only items from a
// `;`-separated string. Used by the emitter when JoinItems' Right is
// a property reference (`$(SatelliteResourceLanguages)`) rather than
// an item-list reference.
func ItemsFromIdentities(s string) []*Item {
	parts := SplitSemicolon(s)
	if len(parts) == 0 {
		return nil
	}
	out := make([]*Item, 0, len(parts))
	for _, p := range parts {
		if p == "" {
			continue
		}
		out = append(out, NewItem(p))
	}
	return out
}

func joinItemKey(key string, it *Item) string {
	if key == "" {
		return it.Identity
	}
	return it.GetMetadata(key)
}

func parseJoinMetaList(raw string) []string {
	parts := SplitSemicolon(raw)
	if len(parts) == 0 {
		return nil
	}
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

func mergeJoinedItem(
	left, right *Item,
	leftKey, rightKey string,
	leftMeta, rightMeta []string,
	useAllLeftMeta, useAllRightMeta bool,
	useLeftItemSpec, useRightItemSpec bool,
) *Item {
	// Fast paths replicating Microsoft.NET.Build.Tasks.JoinItems.
	if useAllLeftMeta && (leftKey == "" || useLeftItemSpec) && len(rightMeta) == 0 {
		return left.Clone()
	}
	if useAllRightMeta && (rightKey == "" || useRightItemSpec) && len(leftMeta) == 0 {
		return right.Clone()
	}

	var id string
	switch {
	case useLeftItemSpec:
		id = left.Identity
	case useRightItemSpec:
		id = right.Identity
	default:
		id = joinItemKey(leftKey, left)
	}
	out := NewItem(id)

	// Order matches the SDK source: useAllLeftMeta → RightMetadata specific
	// → LeftMetadata specific → useAllRightMeta. The two "*" copies strip
	// OriginalItemSpec to avoid contaminating the merged identity trail.
	if useAllLeftMeta {
		left.CopyMetadataTo(out)
		out.RemoveMetadata("OriginalItemSpec")
	}
	if !useAllRightMeta && len(rightMeta) > 0 {
		for _, name := range rightMeta {
			out.SetMetadata(name, right.GetMetadata(name))
		}
	}
	if !useAllLeftMeta && len(leftMeta) > 0 {
		for _, name := range leftMeta {
			out.SetMetadata(name, left.GetMetadata(name))
		}
	}
	if useAllRightMeta {
		right.CopyMetadataTo(out)
		out.RemoveMetadata("OriginalItemSpec")
	}
	return out
}

func ensureTrailingSeparator(p string) string {
	if p == "" {
		return p
	}
	if p[len(p)-1] == os.PathSeparator || p[len(p)-1] == '/' {
		return p
	}
	return p + string(os.PathSeparator)
}

func boolStr(v bool) string {
	if v {
		return "True"
	}
	return "False"
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

// AllowEmptyTelemetry is a no-op in the closed-world build. Real MSBuild
// uses this to emit telemetry events to BuildEngine's IBuildEngine5 hook;
// our host does not collect telemetry, so EventName + EventData are
// ignored entirely.
func AllowEmptyTelemetry(p ParamList) error {
	return nil
}

// CheckForDuplicateNuGetItemsTask dedupes ITaskItem[] inputs by ItemSpec
// (case-insensitive). NuGet wires this for PackageReference + PackageVersion
// to surface NU1504/NU1506 warnings. In the closed-world build we don't have
// the metadata-rich item objects flowing through ParamList (only their
// semicolon-joined identities), so we cannot reproduce the warning text
// or preserve per-item metadata in the output. Behavior here:
//   - Identities are split, deduped by lowercased value.
//   - If no duplicates exist, DeduplicatedItems is left empty so the
//     conditional ItemGroup downstream (Condition="'@(X)' != ''") skips.
//   - If duplicates exist, DeduplicatedItems is set to the deduped
//     identities; downstream Remove/Include cycle replaces the original
//     list. (Metadata is lost, but the resolved package set is correct
//     for our research fixture which has no metadata-bearing dupes.)
func CheckForDuplicateNuGetItemsTask(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	seen := map[string]bool{}
	var deduped []*Item
	hadDup := false
	for _, id := range SplitSemicolon(p.GetValueOrDefault("Items")) {
		if id == "" {
			continue
		}
		key := strings.ToLower(id)
		if seen[key] {
			hadDup = true
			continue
		}
		seen[key] = true
		deduped = append(deduped, NewItem(id))
	}
	if hadDup {
		writeItemOutput(outputs, items, "DeduplicatedItems", deduped)
	}
	return nil
}

// writeItemOutput writes the given items slice to the configured output for
// `key`. If the spec is property-bound rather than item-bound, the items'
// identities are joined with `;` (matching MSBuild's behavior when an
// ITaskItem[] output is bound to a string property). No-ops if outputs is
// nil or `key` is unbound.
// writeItemOutput routes an item-list result through the OutputList
// binding for `key`. When bound to an ItemName, items are appended to
// that item type. When bound to a PropertyName instead, identities are
// joined with `;` per standard MSBuild ITaskItem[]→string coercion.
// No-ops if outputs is nil or `key` is unbound.
func writeItemOutput(outputs *OutputList, items ItemBag, key string, vals []*Item) {
	writeItemOutputProps(outputs, items, nil, key, vals)
}

// writeItemOutputProps is the props-aware variant used by tasks (Exec,
// ConvertToAbsolutePath, …) that may have an item-list output bound to
// either an ItemName or a PropertyName. When a PropertyName is wired,
// identities are joined with `;` and written through PropertyBag.
func writeItemOutputProps(outputs *OutputList, items ItemBag, props PropertyBag, key string, vals []*Item) {
	if outputs == nil {
		return
	}
	spec, ok := outputs.TryGetValue(key)
	if !ok {
		return
	}
	if spec.ItemName != "" {
		items.AppendTo(spec.ItemName, vals)
		return
	}
	if spec.PropertyName != "" && props != nil {
		ids := make([]string, 0, len(vals))
		for _, v := range vals {
			ids = append(ids, v.Identity)
		}
		props.Set(spec.PropertyName, strings.Join(ids, ";"))
	}
}

// writeStringOutput writes a scalar string to the configured output for
// `key`. If the spec is item-bound rather than property-bound, the string is
// promoted to a single Item (matching MSBuild's behavior when a string
// output is bound to an ITaskItem[]). No-ops if outputs is nil or `key`
// is unbound.
func writeStringOutput(outputs *OutputList, items ItemBag, props PropertyBag, key, value string) {
	if outputs == nil {
		return
	}
	spec, ok := outputs.TryGetValue(key)
	if !ok {
		return
	}
	if spec.PropertyName != "" {
		props.Set(spec.PropertyName, value)
	} else if spec.ItemName != "" {
		items.AppendTo(spec.ItemName, []*Item{NewItem(value)})
	}
}

// Exec spawns a subprocess to run the "Command" string via the platform
// shell (`sh -c` on Unix, `cmd /c` on Windows). It captures the exit code,
// optionally captures stdout (when ConsoleToMSBuild is true), and binds
// the well-known outputs:
//   - ExitCode: integer exit code as a string property/item.
//   - ConsoleOutput: only populated when ConsoleToMSBuild is true; each
//     non-empty line becomes an item with that line as Identity. When
//     bound to a PropertyName instead of an ItemName, OutputList collapses
//     the items with `;` per standard MSBuild coercion.
//
// If IgnoreExitCode is "true", a non-zero exit produces no error (the
// caller typically inspects ExitCode itself). Otherwise a non-zero exit
// surfaces as an error so the build fails loudly.
//
// Output is forwarded to bsharp's stdout/stderr live so users see real-time
// progress, even when ConsoleToMSBuild also captures into the output
// buffer (via io.MultiWriter). When neither IgnoreStandardErrorWarningFormat
// nor any of the regex-tuning parameters are honored — bsharp logs the
// command's stderr verbatim and trusts the wrapping target Error="..."
// guards (e.g. !Exists($(Tool))) to surface friendly diagnostics.
func Exec(p ParamList, outputs *OutputList, items ItemBag, props PropertyBag) error {
	command := p.GetValueOrDefault("Command")
	if command == "" {
		return nil
	}
	workDir := p.GetValueOrDefault("WorkingDirectory")
	ignoreExitCode := strings.EqualFold(p.GetValueOrDefault("IgnoreExitCode"), "true")
	consoleCapture := strings.EqualFold(p.GetValueOrDefault("ConsoleToMSBuild"), "true")

	var cmd *exec.Cmd
	if runtime.GOOS == "windows" {
		cmd = exec.Command("cmd.exe", "/c", command)
	} else {
		cmd = exec.Command("sh", "-c", command)
	}
	if workDir != "" {
		cmd.Dir = workDir
	}

	var stdoutBuf bytes.Buffer
	if consoleCapture {
		cmd.Stdout = io.MultiWriter(&stdoutBuf, os.Stdout)
	} else {
		cmd.Stdout = os.Stdout
	}
	cmd.Stderr = os.Stderr

	runErr := cmd.Run()
	exitCode := 0
	if runErr != nil {
		if exitErr, ok := runErr.(*exec.ExitError); ok {
			exitCode = exitErr.ExitCode()
		} else {
			// Spawn failure (binary not found, working dir missing, etc.) — never
			// suppressed by IgnoreExitCode because no exit code was produced.
			return fmt.Errorf("Exec: %s: %w", command, runErr)
		}
	}

	writeStringOutput(outputs, items, props, "ExitCode", strconv.Itoa(exitCode))

	if consoleCapture {
		var lines []*Item
		scanner := bufio.NewScanner(&stdoutBuf)
		scanner.Buffer(make([]byte, 64*1024), 4*1024*1024)
		for scanner.Scan() {
			text := strings.TrimRight(scanner.Text(), "\r")
			if text == "" {
				continue
			}
			lines = append(lines, NewItem(text))
		}
		writeItemOutputProps(outputs, items, props, "ConsoleOutput", lines)
	}

	if !ignoreExitCode && exitCode != 0 {
		return fmt.Errorf("Exec: command exited with code %d: %s", exitCode, command)
	}
	return nil
}
