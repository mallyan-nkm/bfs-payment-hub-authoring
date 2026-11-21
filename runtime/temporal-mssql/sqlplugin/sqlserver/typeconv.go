package sqlserver

import "time"

var (
	minSQLServerDateTime = getMinSQLServerDateTime()
)

type (
	// DataConverter converts between Go times and the DATETIME2 values the plugin stores.
	DataConverter interface {
		ToSQLServerDateTime(t time.Time) time.Time
		FromSQLServerDateTime(t time.Time) time.Time
	}
	converter struct{}
)

// ToSQLServerDateTime keeps the PostgreSQL plugin's mapping: a zero time is stored as 1000-01-01,
// any other time in UTC, to the microsecond.
func (c *converter) ToSQLServerDateTime(t time.Time) time.Time {
	if t.IsZero() {
		return minSQLServerDateTime
	}
	return t.UTC().Truncate(time.Microsecond)
}

// FromSQLServerDateTime reverses ToSQLServerDateTime.
func (c *converter) FromSQLServerDateTime(t time.Time) time.Time {
	if t.Equal(minSQLServerDateTime) {
		return time.Time{}.UTC()
	}
	return t.UTC()
}

func getMinSQLServerDateTime() time.Time {
	t, err := time.Parse(time.RFC3339, "1000-01-01T00:00:00Z")
	if err != nil {
		return time.Unix(0, 0).UTC()
	}
	return t.UTC()
}
