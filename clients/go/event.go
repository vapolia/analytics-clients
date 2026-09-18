package analytics

import (
	"encoding/json"
	"maps"
	"math"
	"slices"
	"strings"
	"time"
	"unicode"
)

// Limits mirrored from the collector's EventSanitizer, which applies them again on arrival.
const (
	MaxEventsPerBatch = 100
	MaxPropsPerEvent  = 12
	MaxContextKeys    = 12
	MaxValueLength    = 64
	maxBuildLength    = 24
	maxOsVersionLen   = 24
	maxLocaleLength   = 12
)

// MaxEventAge is how far back the collector accepts a timestamp.
const MaxEventAge = 7 * 24 * time.Hour

var (
	platforms     = set("android", "ios", "maccatalyst", "windows", "web")
	deviceClasses = set("phone", "tablet", "desktop", "other")
	stores        = set("google", "apple", "other")
)

// Device is what the app reported about the device it runs on. All fields are optional. What is
// true of the installation rather than of the device belongs in the batch context instead.
type Device struct {
	// Build is the store build number, not the display version.
	Build       string
	Platform    string // android | ios | maccatalyst | windows | web
	OsVersion   string
	DeviceClass string // phone | tablet | desktop | other
	Locale      string
	Country     string // ISO 3166-1 alpha-2, from the device locale
	Store       string // google | apple | other
}

// clean returns the device reduced to what the collector will store, and whether the whole batch is
// refused (excluded country).
func (d Device) clean(excluded map[string]bool) (Device, bool) {
	country := cleanCountry(d.Country)
	if excluded[country] {
		return Device{}, false
	}

	return Device{
		Build:       text(d.Build, maxBuildLength),
		Platform:    pick(d.Platform, platforms),
		OsVersion:   text(d.OsVersion, maxOsVersionLen),
		DeviceClass: pick(d.DeviceClass, deviceClasses),
		Locale:      text(d.Locale, maxLocaleLength),
		Country:     country,
		Store:       pick(d.Store, stores),
	}, true
}

func cleanProps(props map[string]any) map[string]any {
	return cleanScalars(props, MaxPropsPerEvent)
}

// cleanScalars serves both an event's props and a batch context: scalars only, capped in count and
// in length.
func cleanScalars(props map[string]any, maxKeys int) map[string]any {
	if len(props) == 0 {
		return nil
	}

	kept := make(map[string]any, min(len(props), maxKeys))
	for _, key := range sortedKeys(props) {
		if len(kept) == maxKeys {
			break
		}

		switch v := props[key].(type) {
		case string:
			if s := text(v, MaxValueLength); s != "" {
				kept[key] = s
			}
		case bool:
			kept[key] = v
		case int:
			kept[key] = float64(v)
		case int32:
			kept[key] = float64(v)
		case int64:
			kept[key] = float64(v)
		case float32:
			addFinite(kept, key, float64(v))
		case float64:
			addFinite(kept, key, v)
		}
	}

	if len(kept) == 0 {
		return nil
	}
	return kept
}

func addFinite(kept map[string]any, key string, v float64) {
	// NaN and the infinities are not representable in jsonb.
	if !math.IsNaN(v) && !math.IsInf(v, 0) {
		kept[key] = v
	}
}

// text trims, strips control characters, caps the length, and turns blank into empty.
func text(value string, maxLength int) string {
	trimmed := strings.TrimSpace(value)
	if trimmed == "" {
		return ""
	}

	runes := []rune(trimmed)
	if len(runes) > maxLength {
		runes = runes[:maxLength]
	}

	cleaned := make([]rune, 0, len(runes))
	for _, r := range runes {
		if !unicode.IsControl(r) {
			cleaned = append(cleaned, r)
		}
	}

	return strings.TrimSpace(string(cleaned))
}

// cleanCountry accepts exactly two ASCII letters, or nothing: truncating would turn "GERMANY" into
// "GE", which is Georgia.
func cleanCountry(value string) string {
	trimmed := strings.TrimSpace(value)
	if len([]rune(trimmed)) != 2 {
		return ""
	}
	for _, r := range trimmed {
		if r > unicode.MaxASCII || !unicode.IsLetter(r) {
			return ""
		}
	}
	return strings.ToUpper(trimmed)
}

func pick(value string, allowed map[string]bool) string {
	normalized := strings.ToLower(strings.TrimSpace(value))
	if allowed[normalized] {
		return normalized
	}
	return ""
}

func set(values ...string) map[string]bool {
	m := make(map[string]bool, len(values))
	for _, v := range values {
		m[v] = true
	}
	return m
}

func sortedKeys(props map[string]any) []string {
	return slices.Sorted(maps.Keys(props))
}

// encodeContext cleans a batch context and returns it as canonical JSON. encoding/json sorts map
// keys, so identical contexts produce identical text, which is what lets the batch key be a string.
func encodeContext(context map[string]any) string {
	kept := cleanScalars(context, MaxContextKeys)
	if len(kept) == 0 {
		return "{}"
	}

	encoded, err := json.Marshal(kept)
	if err != nil {
		return "{}"
	}
	return string(encoded)
}

// cleanTz keeps an offset within the real range, UTC−12:00 to UTC+14:00, and drops anything else.
func cleanTz(tz *int) *int {
	if tz == nil || *tz < -12*60 || *tz > 14*60 {
		return nil
	}
	v := *tz
	return &v
}
