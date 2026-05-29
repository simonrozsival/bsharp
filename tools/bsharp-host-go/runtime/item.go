package runtime

import (
	"path/filepath"
	"runtime"
	"strings"
)

// Item is the Go equivalent of the generated C# `Item` class: an identity
// (typically a path) plus a lowercase-keyed metadata map. Metadata access is
// case-insensitive, matching MSBuild. Several well-known metadata names
// (identity, fullpath, filename, extension, directory, relativedir, rootdir)
// are computed on demand from the identity.
type Item struct {
	Identity string
	meta     map[string]string

	cachedFullPath    string
	cachedDirectory   string
	cachedRelativeDir string
	cachedFullPathOK  bool
	cachedDirectoryOK bool
	cachedRelDirOK    bool
}

// NewItem creates an Item with the given identity. Identity is normalized
// to use the host directory separator (matching the C# Item constructor).
func NewItem(id string) *Item {
	return &Item{Identity: normalizeIdentity(id)}
}

// NewItemWithMetadata creates an Item with both an identity and a copy of
// the supplied metadata. Keys are stored lowercase.
func NewItemWithMetadata(id string, metadata map[string]string) *Item {
	it := &Item{Identity: normalizeIdentity(id)}
	if len(metadata) > 0 {
		it.meta = make(map[string]string, len(metadata))
		for k, v := range metadata {
			it.meta[strings.ToLower(k)] = v
		}
	}
	return it
}

// Metadata returns the underlying metadata map (lazily created). Use SetMetadata
// to mutate; callers must not modify the map directly under concurrent access.
func (i *Item) Metadata() map[string]string {
	if i.meta == nil {
		i.meta = make(map[string]string)
	}
	return i.meta
}

// MetadataOrNil returns the raw map without allocating an empty one.
func (i *Item) MetadataOrNil() map[string]string { return i.meta }

// GetMetadata returns the metadata value for the given name (case-insensitive).
// Well-known names are derived from the identity if not explicitly set.
// Empty string is returned for unknown names.
func (i *Item) GetMetadata(name string) string {
	lname := strings.ToLower(name)
	switch lname {
	case "identity":
		return i.Identity
	case "fullpath":
		if !i.cachedFullPathOK {
			if i.Identity == "" {
				i.cachedFullPath = ""
			} else {
				abs, err := filepath.Abs(i.Identity)
				if err != nil {
					abs = i.Identity
				}
				i.cachedFullPath = abs
			}
			i.cachedFullPathOK = true
		}
		return i.cachedFullPath
	case "filename":
		if i.Identity == "" {
			return ""
		}
		base := filepath.Base(i.Identity)
		ext := filepath.Ext(base)
		return strings.TrimSuffix(base, ext)
	case "extension":
		if i.Identity == "" {
			return ""
		}
		return filepath.Ext(i.Identity)
	case "directory":
		if !i.cachedDirectoryOK {
			if i.Identity == "" {
				i.cachedDirectory = ""
			} else {
				d := filepath.Dir(i.Identity)
				if d == "." {
					d = ""
				}
				i.cachedDirectory = d
			}
			i.cachedDirectoryOK = true
		}
		return i.cachedDirectory
	case "relativedir":
		if !i.cachedRelDirOK {
			if i.Identity == "" {
				i.cachedRelativeDir = ""
			} else {
				d := filepath.Dir(i.Identity)
				if d == "" || d == "." {
					i.cachedRelativeDir = ""
				} else {
					i.cachedRelativeDir = d + string(filepath.Separator)
				}
			}
			i.cachedRelDirOK = true
		}
		return i.cachedRelativeDir
	case "rootdir":
		if i.Identity == "" {
			return ""
		}
		return filepath.VolumeName(i.Identity) + string(filepath.Separator)
	}
	if i.meta == nil {
		return ""
	}
	return i.meta[lname]
}

// HasMetadata returns true iff GetMetadata(name) equals value (case-insensitive).
// Mirrors the C# `Item.HasMetadata` fast path used by generated conditions.
func (i *Item) HasMetadata(name, value string) bool {
	return strings.EqualFold(i.GetMetadata(name), value)
}

// SetMetadata stores the value under the lowercased name. Nil-equivalent values
// become "".
func (i *Item) SetMetadata(name, value string) {
	if i.meta == nil {
		i.meta = make(map[string]string)
	}
	i.meta[strings.ToLower(name)] = value
	switch strings.ToLower(name) {
	case "fullpath":
		i.cachedFullPathOK = false
	case "directory":
		i.cachedDirectoryOK = false
	case "relativedir":
		i.cachedRelDirOK = false
	}
}

// CopyMetadataTo copies all metadata from src to dst. Existing keys in dst
// are overwritten (matches the C# CopyMetadataTo behavior).
func (i *Item) CopyMetadataTo(dst *Item) {
	if i.meta == nil {
		return
	}
	m := dst.Metadata()
	for k, v := range i.meta {
		m[k] = v
	}
}

// Clone returns a deep copy of the item (independent metadata map).
func (i *Item) Clone() *Item {
	c := &Item{Identity: i.Identity}
	if len(i.meta) > 0 {
		c.meta = make(map[string]string, len(i.meta))
		for k, v := range i.meta {
			c.meta[k] = v
		}
	}
	return c
}

// normalizeIdentity converts backslashes to slashes on non-Windows hosts when
// the identity doesn't look like a property assignment (e.g. "Foo=Bar\Baz"),
// matching the C# `Item.NormalizeIdentity` heuristic.
func normalizeIdentity(value string) string {
	if runtime.GOOS == "windows" {
		return value
	}
	if !strings.ContainsRune(value, '\\') {
		return value
	}
	if strings.ContainsRune(value, '=') {
		return value
	}
	return strings.ReplaceAll(value, "\\", "/")
}
