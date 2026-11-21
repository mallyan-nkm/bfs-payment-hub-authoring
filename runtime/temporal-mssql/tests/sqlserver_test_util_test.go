// SQL Server set-up for the persistence suites, after common/persistence/tests/postgresql_test_util.go
// of go.temporal.io/server v1.32.2.

package sqlservertests

import (
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"testing"

	"go.temporal.io/server/common/config"
	"go.temporal.io/server/common/log"
	"go.temporal.io/server/common/metrics"
	"go.temporal.io/server/common/metrics/metricstest"
	p "go.temporal.io/server/common/persistence"
	persistencetests "go.temporal.io/server/common/persistence/persistence-tests"
	"go.temporal.io/server/common/persistence/serialization"
	"go.temporal.io/server/common/persistence/sql"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/resolver"
	"go.temporal.io/server/common/shuffle"
	"go.uber.org/zap/zaptest"

	"github.com/transacthub/temporal-mssql/sqlplugin/sqlserver"
)

const (
	testSQLServerClusterName        = "temporal_sqlserver_cluster"
	testSQLServerDatabaseNamePrefix = "test_"
	testSQLServerDatabaseNameSuffix = "temporal_persistence"
)

// The server under test: SQLSERVER_ADDR (host:port), SQLSERVER_USER (default sa) and
// SQLSERVER_PASSWORD.
func sqlServerAddr() string { return os.Getenv("SQLSERVER_ADDR") }

func sqlServerHost() string {
	h, _, _ := net.SplitHostPort(sqlServerAddr())
	return h
}

func sqlServerPort() int {
	_, port, _ := net.SplitHostPort(sqlServerAddr())
	n, _ := strconv.Atoi(port)
	return n
}

func sqlServerUser() string {
	if u := os.Getenv("SQLSERVER_USER"); u != "" {
		return u
	}
	return "sa"
}

func sqlServerPassword() string { return os.Getenv("SQLSERVER_PASSWORD") }

func requireSQLServer(t *testing.T) {
	if sqlServerAddr() == "" {
		t.Skip("SQLSERVER_ADDR is not set: no SQL Server to run Temporal's persistence suites against")
	}
}

// schemaDir is schema/sqlserver in this module, as an absolute path.
func schemaDir() string {
	_, file, _, _ := runtime.Caller(0)
	return filepath.Join(filepath.Dir(file), "..", "schema", "sqlserver")
}

type SQLServerTestData struct {
	Cfg     *config.SQL
	Factory *sql.Factory
	Logger  log.Logger
	Metrics *metricstest.Capture
}

func setUpSQLServerTest(t *testing.T) (SQLServerTestData, func()) {
	var testData SQLServerTestData
	testData.Cfg = NewSQLServerConfig()
	testData.Logger = log.NewZapLogger(zaptest.NewLogger(t))
	mh := metricstest.NewCaptureHandler()
	testData.Metrics = mh.StartCapture()
	SetupSQLServerDatabase(t, testData.Cfg)
	SetupSQLServerSchema(t, testData.Cfg)

	testData.Factory = sql.NewFactory(
		*testData.Cfg,
		resolver.NewNoopResolver(),
		testSQLServerClusterName,
		testData.Logger,
		mh,
		serialization.NewSerializer(),
	)

	tearDown := func() {
		testData.Factory.Close()
		mh.StopCapture(testData.Metrics)
		TearDownSQLServerDatabase(t, testData.Cfg)
	}
	return testData, tearDown
}

// NewSQLServerConfig returns a config for a new, uniquely named test database.
func NewSQLServerConfig() *config.SQL {
	return &config.SQL{
		User:            sqlServerUser(),
		Password:        sqlServerPassword(),
		ConnectAddr:     sqlServerAddr(),
		ConnectProtocol: "tcp",
		PluginName:      sqlserver.PluginName,
		DatabaseName:    testSQLServerDatabaseNamePrefix + shuffle.String(testSQLServerDatabaseNameSuffix),
	}
}

// GetSQLServerTestClusterOption is persistencetests.GetPostgreSQLTestClusterOption for SQL Server.
func GetSQLServerTestClusterOption() *persistencetests.TestBaseOptions {
	return &persistencetests.TestBaseOptions{
		SQLDBPluginName: sqlserver.PluginName,
		DBName:          persistencetests.GenerateRandomDBName(),
		DBUsername:      sqlServerUser(),
		DBPassword:      sqlServerPassword(),
		DBHost:          sqlServerHost(),
		DBPort:          sqlServerPort(),
		SchemaDir:       schemaDir(),
		StoreType:       config.StoreTypeSQL,
	}
}

func SetupSQLServerDatabase(t *testing.T, cfg *config.SQL) {
	adminCfg := *cfg
	adminCfg.DatabaseName = ""
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, &adminCfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatalf("unable to create SQL Server admin DB: %v", err)
	}
	defer func() { _ = db.Close() }()
	if err := db.CreateDatabase(cfg.DatabaseName); err != nil {
		t.Fatalf("unable to create SQL Server database: %v", err)
	}
}

func SetupSQLServerSchema(t *testing.T, cfg *config.SQL) {
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatalf("unable to create SQL Server admin DB: %v", err)
	}
	defer func() { _ = db.Close() }()
	for _, part := range []string{"temporal", "visibility"} {
		statements, err := p.LoadAndSplitQuery([]string{filepath.Join(schemaDir(), part, "schema.sql")})
		if err != nil {
			t.Fatal(err)
		}
		for _, stmt := range statements {
			if err = db.Exec(stmt); err != nil {
				t.Fatalf("%s schema: %v\n%s", part, err, stmt)
			}
		}
	}
}

func TearDownSQLServerDatabase(t *testing.T, cfg *config.SQL) {
	adminCfg := *cfg
	adminCfg.DatabaseName = ""
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, &adminCfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		t.Fatalf("unable to create SQL Server admin DB: %v", err)
	}
	defer func() { _ = db.Close() }()
	if err := db.DropDatabase(cfg.DatabaseName); err != nil {
		t.Fatalf("unable to drop SQL Server database: %v", err)
	}
}
