package sqlserver

import (
	"fmt"
	"strings"
	"time"
)

const (
	readSchemaVersionQuery = `SELECT curr_version FROM schema_version WHERE version_partition=0 AND db_name=@p1`

	writeSchemaVersionQuery = `MERGE INTO schema_version WITH (HOLDLOCK) AS t
USING (SELECT 0 AS version_partition, CAST(@p1 AS VARCHAR(255)) AS db_name) AS s
ON t.version_partition = s.version_partition AND t.db_name = s.db_name
WHEN MATCHED THEN UPDATE SET creation_time = @p2, curr_version = @p3, min_compatible_version = @p4
WHEN NOT MATCHED THEN INSERT (version_partition, db_name, creation_time, curr_version, min_compatible_version)
VALUES (0, s.db_name, @p2, @p3, @p4);`

	writeSchemaUpdateHistoryQuery = `INSERT INTO schema_update_history(version_partition, year, month, update_time, old_version, new_version, manifest_md5, description) VALUES(0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)`

	createSchemaVersionTableQuery = `IF OBJECT_ID(N'schema_version', N'U') IS NULL CREATE TABLE schema_version(` +
		`version_partition INT NOT NULL, ` +
		`db_name VARCHAR(255) NOT NULL, ` +
		`creation_time DATETIME2(6), ` +
		`curr_version VARCHAR(64), ` +
		`min_compatible_version VARCHAR(64), ` +
		`PRIMARY KEY (version_partition, db_name));`

	createSchemaUpdateHistoryTableQuery = `IF OBJECT_ID(N'schema_update_history', N'U') IS NULL CREATE TABLE schema_update_history(` +
		`version_partition INT NOT NULL, ` +
		`year INT NOT NULL, ` +
		`month INT NOT NULL, ` +
		`update_time DATETIME2(6) NOT NULL, ` +
		`description VARCHAR(255), ` +
		`manifest_md5 VARCHAR(64), ` +
		`new_version VARCHAR(64), ` +
		`old_version VARCHAR(64), ` +
		`PRIMARY KEY (version_partition, year, month, update_time));`

	// DatabaseCollation is the collation every Temporal database on SQL Server is created with:
	// VARCHAR holds UTF-8 and compares byte by byte (case- and accent-sensitive), as on MySQL's
	// utf8mb4_bin and PostgreSQL's default.
	DatabaseCollation = "Latin1_General_100_BIN2_UTF8"

	// The database name is an identifier, not a parameter; quoteName below brackets it.
	createDatabaseQuery = `IF DB_ID(@p1) IS NULL CREATE DATABASE %s COLLATE ` + DatabaseCollation

	// Readers see the last committed version of a row instead of waiting for a writer, as on
	// PostgreSQL. Locking reads (UPDLOCK, REPEATABLEREAD) still read the latest row and lock it.
	setReadCommittedSnapshotQuery = `ALTER DATABASE %s SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE`

	dropDatabaseQuery      = `IF DB_ID(@p1) IS NOT NULL DROP DATABASE %s`
	singleUserQuery        = `IF DB_ID(@p1) IS NOT NULL ALTER DATABASE %s SET SINGLE_USER WITH ROLLBACK IMMEDIATE`
	listTablesQuery        = `SELECT table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema = SCHEMA_NAME()`
	listForeignViewsQuery  = `SELECT table_name FROM information_schema.views WHERE table_schema = SCHEMA_NAME()`
	dropTableQuery         = `DROP TABLE %s`
	dropViewQuery          = `DROP VIEW %s`
	databaseCollationQuery = `SELECT CONVERT(VARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))`
)

// quoteName brackets a SQL Server identifier, doubling any closing bracket in it.
func quoteName(name string) string {
	return "[" + strings.ReplaceAll(name, "]", "]]") + "]"
}

func (pdb *db) Exec(stmt string, args ...any) error {
	db, err := pdb.handle.DB()
	if err != nil {
		return err
	}
	_, err = db.Exec(stmt, convertArgs(args)...)
	return pdb.handle.ConvertError(err)
}

func (pdb *db) CreateSchemaVersionTables() error {
	if err := pdb.Exec(createSchemaVersionTableQuery); err != nil {
		return err
	}
	return pdb.Exec(createSchemaUpdateHistoryTableQuery)
}

func (pdb *db) ReadSchemaVersion(database string) (string, error) {
	var version string
	db, err := pdb.handle.DB()
	if err != nil {
		return "", err
	}
	err = db.Get(&version, readSchemaVersionQuery, database)
	return version, pdb.handle.ConvertError(err)
}

func (pdb *db) UpdateSchemaVersion(database string, newVersion string, minCompatibleVersion string) error {
	return pdb.Exec(writeSchemaVersionQuery, database, time.Now().UTC(), newVersion, minCompatibleVersion)
}

func (pdb *db) WriteSchemaUpdateLog(oldVersion string, newVersion string, manifestMD5 string, desc string) error {
	now := time.Now().UTC()
	err := pdb.Exec(writeSchemaUpdateHistoryQuery, now.Year(), int(now.Month()), now, oldVersion, newVersion, manifestMD5, desc)
	return pdb.handle.ConvertError(err)
}

func (pdb *db) ListTables(database string) ([]string, error) {
	var tables []string
	err := pdb.Select(&tables, listTablesQuery)
	return tables, pdb.handle.ConvertError(err)
}

func (pdb *db) DropTable(name string) error {
	return pdb.Exec(fmt.Sprintf(dropTableQuery, quoteName(name)))
}

func (pdb *db) DropAllTables(database string) error {
	var views []string
	if err := pdb.Select(&views, listForeignViewsQuery); err != nil {
		return err
	}
	for _, v := range views {
		if err := pdb.Exec(fmt.Sprintf(dropViewQuery, quoteName(v))); err != nil {
			return err
		}
	}
	tables, err := pdb.ListTables(database)
	if err != nil {
		return err
	}
	for _, tab := range tables {
		if err := pdb.DropTable(tab); err != nil {
			return err
		}
	}
	return nil
}

// CreateDatabase creates the database with Temporal's collation and snapshot reads. An existing
// database is kept, but its collation is checked: the schema needs the UTF-8 binary collation.
func (pdb *db) CreateDatabase(name string) error {
	if err := pdb.Exec(fmt.Sprintf(createDatabaseQuery, quoteName(name)), name); err != nil {
		if pdb.IsDupDatabaseError(err) {
			return nil
		}
		return err
	}
	return pdb.Exec(fmt.Sprintf(setReadCommittedSnapshotQuery, quoteName(name)))
}

func (pdb *db) DropDatabase(name string) error {
	if err := pdb.Exec(fmt.Sprintf(singleUserQuery, quoteName(name)), name); err != nil {
		return err
	}
	return pdb.Exec(fmt.Sprintf(dropDatabaseQuery, quoteName(name)), name)
}

// ForceDropDatabase drops the database even when connections are open (SINGLE_USER WITH ROLLBACK
// IMMEDIATE ends them first).
func (pdb *db) ForceDropDatabase(name string) error {
	return pdb.DropDatabase(name)
}
