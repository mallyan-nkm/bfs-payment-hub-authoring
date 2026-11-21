// Tests of the plugin's own SQL Server behaviour, beyond Temporal's suites.

package sqlservertests

import (
	"context"
	"strings"
	"testing"

	"go.temporal.io/server/common/log"
	"go.temporal.io/server/common/metrics"
	"go.temporal.io/server/common/persistence/sql"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/resolver"
)

// The server checks the schema version at start (VerifyVersion). On SQL Server that check also
// refuses a database whose collation is not the UTF-8 binary one the schema needs, and
// create-database makes a database with it and with snapshot reads.
func TestSQLServerPluginDatabaseSettings(t *testing.T) {
	requireSQLServer(t)
	cfg := NewSQLServerConfig()
	admin := *cfg
	admin.DatabaseName = ""
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, &admin, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = db.Close() }()

	// A database made without the plugin, in the server's default collation.
	plain := cfg.DatabaseName + "_plain"
	if err := db.Exec("CREATE DATABASE [" + plain + "]"); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.DropDatabase(plain) })
	plainCfg := *cfg
	plainCfg.DatabaseName = plain
	pdb, err := sql.NewSQLAdminDB(sqlplugin.DbKindMain, &plainCfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatal(err)
	}
	err = pdb.VerifyVersion()
	_ = pdb.Close()
	if err == nil || !strings.Contains(err.Error(), "Latin1_General_100_BIN2_UTF8") {
		t.Fatalf("VerifyVersion on a database in another collation: got %v, want a refusal naming the collation", err)
	}

	// A database made by the plugin.
	SetupSQLServerDatabase(t, cfg)
	t.Cleanup(func() { TearDownSQLServerDatabase(t, cfg) })
	var settings []struct {
		Collation string `db:"collation_name"`
		RCSI      bool   `db:"is_read_committed_snapshot_on"`
	}
	sdb, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, &admin, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = sdb.Close() }()
	if err := sdb.(interface {
		SelectContext(context.Context, any, string, ...any) error
	}).SelectContext(t.Context(), &settings,
		"SELECT collation_name, is_read_committed_snapshot_on FROM sys.databases WHERE name = @p1", cfg.DatabaseName); err != nil {
		t.Fatal(err)
	}
	if len(settings) != 1 || settings[0].Collation != "Latin1_General_100_BIN2_UTF8" || !settings[0].RCSI {
		t.Fatalf("create-database made %+v", settings)
	}
}
