// SQL Server set-up for the persistence suites, after common/persistence/tests/postgresql_test_util.go
// of go.temporal.io/server v1.32.2.

package sqlservertests

import (
	"fmt"
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
// SQLSERVER_PASSWORD, the login the stores under test connect with.
//
// With SQLSERVER_ADMIN_USER and SQLSERVER_ADMIN_PASSWORD, schema work is the admin's and the stores run as a
// least-privilege login (docs/ISSUES.md I-47): the admin creates each test database and its schema, then gives
// SQLSERVER_USER, which must be a login with no server role, read, write and execute in that database only.
// Without them the one login does both, as before.
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

func sqlServerAdminUser() string {
	if u := os.Getenv("SQLSERVER_ADMIN_USER"); u != "" {
		return u
	}
	return sqlServerUser()
}

func sqlServerAdminPassword() string {
	if pw := os.Getenv("SQLSERVER_ADMIN_PASSWORD"); pw != "" {
		return pw
	}
	return sqlServerPassword()
}

// leastPrivilege says whether the stores run as a login other than the admin's.
func leastPrivilege() bool { return sqlServerAdminUser() != sqlServerUser() }

// asAdmin is cfg with the admin's credentials: what creates, sets up and drops a database.
func asAdmin(cfg *config.SQL) *config.SQL {
	admin := *cfg
	admin.User = sqlServerAdminUser()
	admin.Password = sqlServerAdminPassword()
	return &admin
}

// grantService gives the stores' login read, write and execute in one database, as a bank's DBA would (I-47).
func grantService(t *testing.T, cfg *config.SQL) {
	if err := grantServiceLogin(cfg); err != nil {
		t.Fatal(err)
	}
}

func grantServiceLogin(cfg *config.SQL) error {
	if !leastPrivilege() {
		return nil
	}
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, asAdmin(cfg), resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		return fmt.Errorf("unable to connect as the admin to grant the service login: %w", err)
	}
	defer func() { _ = db.Close() }()
	for _, stmt := range ServiceGrants(sqlServerUser()) {
		if err := db.Exec(stmt); err != nil {
			return fmt.Errorf("grant to the service login: %w\n%s", err, stmt)
		}
	}
	return nil
}

// ServiceGrants is what a least-privilege login needs in each Temporal database: a user, the data reader and writer
// roles, and execute. No server role, no DDL, no CONTROL; schema set-up and upgrades stay with the admin (I-47).
func ServiceGrants(login string) []string {
	q := "[" + login + "]"
	return []string{
		"IF USER_ID(N'" + login + "') IS NULL CREATE USER " + q + " FOR LOGIN " + q,
		"ALTER ROLE db_datareader ADD MEMBER " + q,
		"ALTER ROLE db_datawriter ADD MEMBER " + q,
		"GRANT EXECUTE TO " + q,
	}
}

// leastPrivilegeCluster is Temporal's SQL test cluster with the admin doing the database's set-up and tear-down, and
// the suites' stores connecting as the service login. Once the stores have their configuration, the test base holds
// Temporal's own cluster again: some suites choose their SQL cases by its type, and its tear-down is the admin's.
type leastPrivilegeCluster struct {
	inner *sql.TestCluster
	cfg   *config.SQL
	tb    *persistencetests.TestBase
}

func (c *leastPrivilegeCluster) SetupTestDatabase() {
	c.inner.SetupTestDatabase()
	if err := grantServiceLogin(c.cfg); err != nil {
		panic(err)
	}
}

func (c *leastPrivilegeCluster) TearDownTestDatabase() { c.inner.TearDownTestDatabase() }

func (c *leastPrivilegeCluster) Config() config.Persistence {
	cfg := c.inner.Config()
	stores := make(map[string]config.DataStore, len(cfg.DataStores))
	for name, ds := range cfg.DataStores {
		if ds.SQL != nil {
			sqlCfg := *ds.SQL
			sqlCfg.User = sqlServerUser()
			sqlCfg.Password = sqlServerPassword()
			ds.SQL = &sqlCfg
		}
		stores[name] = ds
	}
	cfg.DataStores = stores
	if c.tb != nil {
		c.tb.DefaultTestCluster = c.inner
	}
	return cfg
}

// newSQLServerTestBase is persistencetests.NewTestBaseWithSQL for SQL Server, with the admin and service logins split.
func newSQLServerTestBase() *persistencetests.TestBase {
	o := GetSQLServerTestClusterOption()
	logger := log.NewTestLogger()
	inner := sql.NewTestCluster(o.SQLDBPluginName, o.DBName, sqlServerAdminUser(), sqlServerAdminPassword(), o.DBHost, o.DBPort,
		o.ConnectAttributes, o.SchemaDir, o.FaultInjection, logger)
	if !leastPrivilege() {
		return persistencetests.NewTestBaseForCluster(inner, logger)
	}
	cfg := &config.SQL{User: sqlServerAdminUser(), Password: sqlServerAdminPassword(), ConnectAddr: sqlServerAddr(), ConnectProtocol: "tcp",
		PluginName: sqlserver.PluginName, DatabaseName: o.DBName}
	c := &leastPrivilegeCluster{inner: inner, cfg: cfg}
	c.tb = persistencetests.NewTestBaseForCluster(c, logger)
	return c.tb
}

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
	adminCfg := *asAdmin(cfg)
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
	defer grantService(t, cfg)
	db, err := sql.NewSQLAdminDB(sqlplugin.DbKindUnknown, asAdmin(cfg), resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
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
	adminCfg := *asAdmin(cfg)
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
