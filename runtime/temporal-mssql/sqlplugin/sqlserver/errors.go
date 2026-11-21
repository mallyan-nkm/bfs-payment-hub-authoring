package sqlserver

import (
	"database/sql/driver"
	"errors"
	"net"
	"strings"

	mssql "github.com/microsoft/go-mssqldb"
)

// SQL Server error numbers the plugin acts on.
const (
	errUniqueConstraint  = 2627 // Violation of PRIMARY KEY or UNIQUE constraint
	errUniqueIndex       = 2601 // Cannot insert duplicate key row in object with unique index
	errDatabaseExists    = 1801 // Database already exists
	errLoginFailed       = 18456
	errDatabaseNotOnline = 942 // Database cannot be opened because it is offline
)

func sqlErrorNumber(err error) (int32, bool) {
	var e mssql.Error
	if errors.As(err, &e) {
		return e.Number, true
	}
	var pe *mssql.Error
	if errors.As(err, &pe) && pe != nil {
		return pe.Number, true
	}
	return 0, false
}

func isDupEntryError(err error) bool {
	n, ok := sqlErrorNumber(err)
	return ok && (n == errUniqueConstraint || n == errUniqueIndex)
}

func isDupDatabaseError(err error) bool {
	n, ok := sqlErrorNumber(err)
	return ok && n == errDatabaseExists
}

// isConnNeedsRefreshError reports an error after which the connection pool should be rebuilt:
// a broken connection, or a login that may succeed with a refreshed password.
func isConnNeedsRefreshError(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, driver.ErrBadConn) {
		return true
	}
	if n, ok := sqlErrorNumber(err); ok {
		return n == errLoginFailed || n == errDatabaseNotOnline
	}
	var ne net.Error
	if errors.As(err, &ne) {
		return true
	}
	msg := err.Error()
	return strings.Contains(msg, "use of closed network connection") || strings.Contains(msg, "connection reset by peer")
}
