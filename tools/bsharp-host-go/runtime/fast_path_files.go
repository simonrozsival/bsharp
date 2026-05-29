package runtime

import (
	"os"
	"path/filepath"
	"strings"
)

// EnumerateProjectSourceFiles returns absolute paths of all `*.cs` files under
// projectDir, skipping the bin/, obj/, and .bsharp/ directories at any depth.
// Mirrors `FastPathFileHelpers.EnumerateProjectSourceFiles`.
func EnumerateProjectSourceFiles(projectDir string) []string {
	var out []string
	stack := []string{projectDir}
	for len(stack) > 0 {
		dir := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		entries, err := os.ReadDir(dir)
		if err != nil {
			continue
		}
		for _, e := range entries {
			name := e.Name()
			full := filepath.Join(dir, name)
			if e.IsDir() {
				if strings.EqualFold(name, "bin") || strings.EqualFold(name, "obj") || strings.EqualFold(name, ".bsharp") {
					continue
				}
				stack = append(stack, full)
				continue
			}
			if strings.EqualFold(filepath.Ext(name), ".cs") {
				out = append(out, full)
			}
		}
	}
	return out
}

// HasProjectSourceNewerThanOutput returns true iff any `*.cs` file under
// projectDir has a mtime > outputTime. Skips bin/, obj/, .bsharp/.
func HasProjectSourceNewerThanOutput(projectDir string, outputTime int64) bool {
	stack := []string{projectDir}
	for len(stack) > 0 {
		dir := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		entries, err := os.ReadDir(dir)
		if err != nil {
			continue
		}
		for _, e := range entries {
			name := e.Name()
			full := filepath.Join(dir, name)
			if e.IsDir() {
				if strings.EqualFold(name, "bin") || strings.EqualFold(name, "obj") || strings.EqualFold(name, ".bsharp") {
					continue
				}
				stack = append(stack, full)
				continue
			}
			if strings.EqualFold(filepath.Ext(name), ".cs") {
				if fi, err := e.Info(); err == nil && fi.ModTime().UnixNano() > outputTime {
					return true
				}
			}
		}
	}
	return false
}

// HasShapeInputNewerThanOutput walks up the directory tree from projectDir
// looking for `Directory.Build.{props,targets}`, `Directory.Packages.props`,
// or `global.json` newer than outputTime. Mirrors the C# fast-path check.
func HasShapeInputNewerThanOutput(projectDir string, outputTime int64) bool {
	dir := projectDir
	for dir != "" {
		for _, name := range []string{"Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"} {
			p := filepath.Join(dir, name)
			if fi, err := os.Stat(p); err == nil {
				if fi.ModTime().UnixNano() > outputTime {
					return true
				}
			}
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	return false
}
