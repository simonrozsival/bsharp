package runtime

import "strings"

// BatchKeys is the Go equivalent of `BatchRuntime.Keys`. It returns the
// distinct values (case-insensitive) of the named metadata across all
// source-item slices. The special name "identity" yields the items'
// identities. Empty values are skipped for non-identity metadata.
func BatchKeys(metadataName string, sources ...[]*Item) []string {
	lname := strings.ToLower(metadataName)
	var keys []string
	seen := make(map[string]struct{})
	for _, source := range sources {
		for _, item := range source {
			key := item.GetMetadata(lname)
			if lname != "identity" && key == "" {
				continue
			}
			lk := strings.ToLower(key)
			if _, ok := seen[lk]; ok {
				continue
			}
			seen[lk] = struct{}{}
			keys = append(keys, key)
		}
	}
	return keys
}

// HasBatchMetadata reports whether the items have any (non-empty for
// non-identity metadata) value for the named metadata.
func HasBatchMetadata(items []*Item, metadataName string) bool {
	lname := strings.ToLower(metadataName)
	if lname == "identity" {
		return len(items) > 0
	}
	for _, item := range items {
		if item.GetMetadata(lname) != "" {
			return true
		}
	}
	return false
}

// ItemsForBatch returns the items whose named metadata equals key
// (case-insensitive). If the items don't carry the requested metadata at all,
// all items are returned (matches the C# "unsplittable" fallback).
func ItemsForBatch(items []*Item, metadataName, key string) []*Item {
	if !HasBatchMetadata(items, metadataName) {
		return items
	}
	out := make([]*Item, 0, len(items))
	for _, item := range items {
		if strings.EqualFold(item.GetMetadata(metadataName), key) {
			out = append(out, item)
		}
	}
	return out
}
