package sqlserver

import (
	"context"
	"database/sql"
	"fmt"
	"reflect"
	"strings"
	"time"

	"github.com/golang-sql/civil"
	"github.com/jmoiron/sqlx"
	"go.temporal.io/server/common/config"
	"go.temporal.io/server/common/log"
	"go.temporal.io/server/common/persistence/schema"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/resolver"
)

// SQL Server accepts at most 2,100 parameters in one request. A batch insert is cut into requests
// that stay under this.
const maxParamsPerRequest = 2000

type db struct {
	dbKind sqlplugin.DbKind
	dbName string

	plugin    *plugin
	cfg       *config.SQL
	resolver  resolver.ServiceResolver
	converter DataConverter
	logger    log.Logger

	handle *sqlplugin.DatabaseHandle
	tx     *sqlx.Tx
}

var _ sqlplugin.DB = (*db)(nil)
var _ sqlplugin.AdminDB = (*db)(nil)

func newDB(
	dbKind sqlplugin.DbKind,
	dbName string,
	handle *sqlplugin.DatabaseHandle,
	tx *sqlx.Tx,
	logger log.Logger,
) *db {
	return &db{
		dbKind:    dbKind,
		dbName:    dbName,
		handle:    handle,
		tx:        tx,
		logger:    logger,
		converter: &converter{},
	}
}

func (pdb *db) IsDupEntryError(err error) bool {
	return isDupEntryError(err)
}

func (pdb *db) IsDupDatabaseError(err error) bool {
	return isDupDatabaseError(err)
}

func (pdb *db) conn() sqlplugin.Conn {
	if pdb.tx != nil {
		return pdb.tx
	}
	return pdb.handle.Conn()
}

// BeginTx starts a transaction at READ COMMITTED. The database is created with
// READ_COMMITTED_SNAPSHOT ON, so plain reads see the last committed row (as on PostgreSQL) and
// the plugin's locking reads (UPDLOCK, REPEATABLEREAD, HOLDLOCK) lock what they read.
func (pdb *db) BeginTx(ctx context.Context) (sqlplugin.Tx, error) {
	db, err := pdb.handle.DB()
	if err != nil {
		return nil, err
	}
	tx, err := db.BeginTxx(ctx, nil)
	if err != nil {
		return nil, pdb.handle.ConvertError(err)
	}
	return newDB(pdb.dbKind, pdb.dbName, pdb.handle, tx, pdb.logger), nil
}

func (pdb *db) Close() error {
	pdb.handle.Close()
	return nil
}

func (pdb *db) PluginName() string {
	return PluginName
}

func (pdb *db) DbName() string {
	return pdb.dbName
}

func (pdb *db) ExpectedVersion() string {
	switch pdb.dbKind {
	case sqlplugin.DbKindMain:
		return Version
	case sqlplugin.DbKindVisibility:
		return VisibilityVersion
	default:
		panic(fmt.Sprintf("unknown db kind %v", pdb.dbKind))
	}
}

// VerifyVersion checks the schema version and that the database has the collation the schema
// needs.
func (pdb *db) VerifyVersion() error {
	var collation string
	if err := pdb.GetContext(context.Background(), &collation, databaseCollationQuery); err != nil {
		return err
	}
	if !strings.EqualFold(collation, DatabaseCollation) {
		return fmt.Errorf("database %q has collation %q; Temporal on SQL Server needs %q (CREATE DATABASE ... COLLATE %s)",
			pdb.dbName, collation, DatabaseCollation, DatabaseCollation)
	}
	return schema.VerifyCompatibleVersion(pdb, pdb.dbName, pdb.ExpectedVersion())
}

func (pdb *db) Commit() error {
	return pdb.tx.Commit()
}

func (pdb *db) Rollback() error {
	return pdb.tx.Rollback()
}

func (pdb *db) ExecContext(ctx context.Context, stmt string, args ...any) (sql.Result, error) {
	res, err := pdb.conn().ExecContext(ctx, stmt, convertArgs(args)...)
	return res, pdb.handle.ConvertError(err)
}

func (pdb *db) GetContext(ctx context.Context, dest any, query string, args ...any) error {
	err := pdb.conn().GetContext(ctx, dest, query, convertArgs(args)...)
	return pdb.handle.ConvertError(err)
}

func (pdb *db) Select(dest any, query string, args ...any) error {
	db, err := pdb.handle.DB()
	if err != nil {
		return err
	}
	err = db.Select(dest, query, convertArgs(args)...)
	return pdb.handle.ConvertError(err)
}

func (pdb *db) SelectContext(ctx context.Context, dest any, query string, args ...any) error {
	err := pdb.conn().SelectContext(ctx, dest, query, convertArgs(args)...)
	return pdb.handle.ConvertError(err)
}

type namedBinder interface {
	BindNamed(query string, arg any) (string, []any, error)
}

// bindNamed compiles a named query (":field") for one struct, a map or a slice of them, with the
// connection's field mapper, into an @pN query and its arguments.
func (pdb *db) bindNamed(query string, arg any) (string, []any, error) {
	b, ok := pdb.conn().(namedBinder)
	if !ok {
		return "", nil, sqlplugin.DatabaseUnavailableError
	}
	q, args, err := b.BindNamed(query, arg)
	if err != nil {
		return "", nil, err
	}
	return q, convertArgs(args), nil
}

// NamedExecContext runs a named statement. For a slice (a batch insert, which sqlx expands into one
// VALUES list), the batch is cut so that no request has more than maxParamsPerRequest parameters.
func (pdb *db) NamedExecContext(ctx context.Context, query string, arg any) (sql.Result, error) {
	v := reflect.ValueOf(arg)
	for v.Kind() == reflect.Pointer && !v.IsNil() {
		v = v.Elem()
	}
	if v.Kind() != reflect.Slice && v.Kind() != reflect.Array {
		q, args, err := pdb.bindNamed(query, arg)
		if err != nil {
			return nil, err
		}
		return pdb.execRaw(ctx, q, args)
	}
	n := v.Len()
	if n == 0 {
		return nil, fmt.Errorf("sqlserver: empty batch for %q", firstLine(query))
	}
	_, one, err := pdb.bindNamed(query, v.Index(0).Interface())
	if err != nil {
		return nil, err
	}
	perChunk := n
	if len(one) > 0 {
		perChunk = max(1, maxParamsPerRequest/len(one))
	}
	var total rowsResult
	for start := 0; start < n; start += perChunk {
		end := min(n, start+perChunk)
		q, args, err := pdb.bindNamed(query, v.Slice(start, end).Interface())
		if err != nil {
			return nil, err
		}
		res, err := pdb.execRaw(ctx, q, args)
		if err != nil {
			return nil, err
		}
		if err := total.add(res); err != nil {
			return nil, err
		}
	}
	return total, nil
}

// execUpsert runs a one-row MERGE once per row of a slice (or once for a single row), summing the
// rows affected. The callers hold a transaction, so the rows commit together.
func (pdb *db) execUpsert(ctx context.Context, query string, arg any) (sql.Result, error) {
	v := reflect.ValueOf(arg)
	if v.Kind() != reflect.Slice && v.Kind() != reflect.Array {
		return pdb.NamedExecContext(ctx, query, arg)
	}
	var total rowsResult
	for i := 0; i < v.Len(); i++ {
		q, args, err := pdb.bindNamed(query, v.Index(i).Interface())
		if err != nil {
			return nil, err
		}
		res, err := pdb.execRaw(ctx, q, args)
		if err != nil {
			return nil, err
		}
		if err := total.add(res); err != nil {
			return nil, err
		}
	}
	return total, nil
}

func (pdb *db) execRaw(ctx context.Context, query string, args []any) (sql.Result, error) {
	res, err := pdb.conn().ExecContext(ctx, query, args...)
	return res, pdb.handle.ConvertError(err)
}

func (pdb *db) PrepareNamedContext(ctx context.Context, query string) (*sqlx.NamedStmt, error) {
	stmt, err := pdb.conn().PrepareNamedContext(ctx, query)
	return stmt, pdb.handle.ConvertError(err)
}

func (pdb *db) QueryContext(ctx context.Context, query string, args ...any) (*sql.Rows, error) {
	db, err := pdb.handle.DB()
	if err != nil {
		return nil, err
	}
	rows, err := db.QueryContext(ctx, query, convertArgs(args)...)
	return rows, pdb.handle.ConvertError(err)
}

func (pdb *db) Rebind(query string) string {
	return pdb.conn().Rebind(query)
}

// rowsResult sums the rows affected by several requests.
type rowsResult int64

func (r *rowsResult) add(res sql.Result) error {
	n, err := res.RowsAffected()
	if err != nil {
		return err
	}
	*r += rowsResult(n)
	return nil
}

func (r rowsResult) LastInsertId() (int64, error) {
	return 0, fmt.Errorf("sqlserver: LastInsertId is not supported")
}

func (r rowsResult) RowsAffected() (int64, error) {
	return int64(r), nil
}

// convertArgs makes arguments match the schema's types. A time is sent as DATETIME2 in UTC: the
// driver would send a time.Time as DATETIMEOFFSET, and converting that to a DATETIME2 column keeps
// the local clock time, not the instant.
func convertArgs(args []any) []any {
	for i, a := range args {
		switch v := a.(type) {
		case time.Time:
			args[i] = toDateTime2(v)
		case *time.Time:
			if v != nil {
				args[i] = toDateTime2(*v)
			}
		}
	}
	return args
}

func toDateTime2(t time.Time) civil.DateTime {
	return civil.DateTimeOf(t.UTC())
}

func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return s[:i]
	}
	return s
}
