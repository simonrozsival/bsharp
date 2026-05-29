package runtime

import (
	"path/filepath"
	"strings"
)

// ItemSpec mirrors the daemon-protocol `ItemSpec`: an identity plus optional
// metadata. Kept here (not in the taskd package) so the runtime can build
// daemon payloads without a cyclic import.
type ItemSpec struct {
	Identity string            `json:"Identity"`
	Metadata map[string]string `json:"Metadata,omitempty"`
}

// OneItemSpec wraps a single Item as an ItemSpec.
func OneItemSpec(it *Item) ItemSpec {
	spec := ItemSpec{Identity: it.Identity}
	if m := it.MetadataOrNil(); len(m) > 0 {
		spec.Metadata = make(map[string]string, len(m))
		for k, v := range m {
			spec.Metadata[k] = v
		}
	}
	return spec
}

// ItemsToSpecs converts a slice of Items to ItemSpecs.
func ItemsToSpecs(items []*Item) []ItemSpec {
	if len(items) == 0 {
		return nil
	}
	out := make([]ItemSpec, len(items))
	for i, it := range items {
		out[i] = OneItemSpec(it)
	}
	return out
}

// ConcatSpecs concatenates multiple ItemSpec slices.
func ConcatSpecs(groups ...[]ItemSpec) []ItemSpec {
	total := 0
	for _, g := range groups {
		total += len(g)
	}
	if total == 0 {
		return nil
	}
	out := make([]ItemSpec, 0, total)
	for _, g := range groups {
		out = append(out, g...)
	}
	return out
}

// ItemsToSpecsWithIdentity is the Go equivalent of `ItemSerde.ToSpecsWithIdentity`:
// keeps metadata but overrides each spec's identity via the supplied selector
// (typically the item's Filename/Directory transform).
func ItemsToSpecsWithIdentity(items []*Item, identitySelector func(*Item) string) []ItemSpec {
	if len(items) == 0 {
		return nil
	}
	out := make([]ItemSpec, len(items))
	for i, it := range items {
		spec := OneItemSpec(it)
		spec.Identity = NormalizeSeparators(identitySelector(it))
		out[i] = spec
	}
	return out
}

// CloneItems returns a deep-cloned slice of items.
func CloneItems(items []*Item) []*Item {
	if len(items) == 0 {
		return nil
	}
	out := make([]*Item, len(items))
	for i, it := range items {
		out[i] = it.Clone()
	}
	return out
}

// SpecsFromScalar parses a `;`-separated list of identities into ItemSpecs.
// Mirrors `ItemSerde.SpecsFromScalar`.
func SpecsFromScalar(semicolonList string) []ItemSpec {
	parts := SplitSemicolon(semicolonList)
	if len(parts) == 0 {
		return nil
	}
	out := make([]ItemSpec, len(parts))
	for i, p := range parts {
		out[i] = ItemSpec{Identity: NormalizeSeparators(p)}
	}
	return out
}

// ItemsFromSpecs builds a deduplicated []*Item from a slice of ItemSpecs.
// Deduplication is by (identity, sorted metadata) — matches MSBuild item
// equality semantics.
func ItemsFromSpecs(specs []ItemSpec) []*Item {
	out := make([]*Item, 0, len(specs))
	seen := make(map[string]struct{}, len(specs))
	for _, s := range specs {
		it := NewItem(NormalizeSeparators(s.Identity))
		for k, v := range s.Metadata {
			it.SetMetadata(k, v)
		}
		key := dedupeKey(it)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, it)
	}
	return out
}

func dedupeKey(it *Item) string {
	var b strings.Builder
	b.WriteString(it.Identity)
	b.WriteByte('|')
	m := it.MetadataOrNil()
	if len(m) == 0 {
		return b.String()
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	// Sort keys for stable comparison.
	for i := 1; i < len(keys); i++ {
		for j := i; j > 0 && keys[j-1] > keys[j]; j-- {
			keys[j-1], keys[j] = keys[j], keys[j-1]
		}
	}
	for i, k := range keys {
		if i > 0 {
			b.WriteByte(0)
		}
		b.WriteString(k)
		b.WriteByte('=')
		b.WriteString(m[k])
	}
	return b.String()
}

// AbsPath returns filepath.Abs(p) but tolerates errors by returning p.
func AbsPath(p string) string {
	abs, err := filepath.Abs(p)
	if err != nil {
		return p
	}
	return abs
}
