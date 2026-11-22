// The persistence suites of go.temporal.io/server v1.32.2 against SQL Server. This file is
// common/persistence/tests/postgresql_test.go of that release, with the PostgreSQL set-up replaced by
// SQL Server's (sqlserver_test_util_test.go) and the suite constructors taken from Temporal's
// exported tests package. Every suite the PostgreSQL wiring runs is run here.
//
//	SQLSERVER_ADDR=127.0.0.1:1433 SQLSERVER_PASSWORD=... go test ./tests/ -run TestSQLServer -v
//
// Without SQLSERVER_ADDR the suites are skipped.

package sqlservertests

import (
	"math"
	"testing"

	"github.com/stretchr/testify/suite"
	"go.temporal.io/server/chasm"
	"go.temporal.io/server/common/log"
	"go.temporal.io/server/common/metrics"
	persistencetests "go.temporal.io/server/common/persistence/persistence-tests"
	"go.temporal.io/server/common/persistence/serialization"
	"go.temporal.io/server/common/persistence/sql"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	sqltests "go.temporal.io/server/common/persistence/sql/sqlplugin/tests"
	ptests "go.temporal.io/server/common/persistence/tests"
	"go.temporal.io/server/common/resolver"

	_ "github.com/transacthub/temporal-mssql/sqlplugin/sqlserver" // register the plugin
)

type SQLServerSuite struct {
	suite.Suite
}

func (p *SQLServerSuite) TestSQLServerShardStoreSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	shardStore, err := testData.Factory.NewShardStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewShardSuite(
		p.T(),
		shardStore,
		serialization.NewSerializer(),
		testData.Logger,
	)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerExecutionMutableStateStoreSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	shardStore, err := testData.Factory.NewShardStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	executionStore, err := testData.Factory.NewExecutionStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	db, err := sql.NewSQLDB(sqlplugin.DbKindMain, testData.Cfg, resolver.NewNoopResolver(), testData.Logger, metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() { _ = db.Close() }()

	s := ptests.NewExecutionMutableStateSuite(
		p.T(),
		shardStore,
		executionStore,
		serialization.NewSerializer(),
		testData.Logger,
	)
	s.MutableStateTableCounts = sqlMutableStateTableCounts(db)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerExecutionMutableStateTaskStoreSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	shardStore, err := testData.Factory.NewShardStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	executionStore, err := testData.Factory.NewExecutionStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewExecutionMutableStateTaskSuite(
		p.T(),
		shardStore,
		executionStore,
		serialization.NewSerializer(),
		testData.Logger,
	)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryStoreSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	store, err := testData.Factory.NewExecutionStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewHistoryEventsSuite(p.T(), store, testData.Logger)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerTaskQueueSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	taskQueueStore, err := testData.Factory.NewTaskStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewTaskQueueSuite(p.T(), taskQueueStore, testData.Logger)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerFairTaskQueueSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	taskQueueStore, err := testData.Factory.NewFairTaskStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewTaskQueueSuite(p.T(), taskQueueStore, testData.Logger) // same suite, different store
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerTaskQueueTaskSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	taskQueueStore, err := testData.Factory.NewTaskStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewTaskQueueTaskSuite(p.T(), taskQueueStore, testData.Logger)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerTaskQueueFairTaskSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	taskQueueStore, err := testData.Factory.NewFairTaskStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewTaskQueueFairTaskSuite(p.T(), taskQueueStore, testData.Logger)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerTaskQueueUserDataSuite() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	taskQueueStore, err := testData.Factory.NewTaskStore()
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}

	s := ptests.NewTaskQueueUserDataSuite(p.T(), taskQueueStore, testData.Logger)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerVisibilityPersistenceSuite() {
	s := &ptests.VisibilityPersistenceSuite{
		TestBase: newSQLServerTestBase(),
	}
	suite.Run(p.T(), s)
}

// TODO: Merge persistence-tests into the tests directory.

func (p *SQLServerSuite) TestSQLServerHistoryV2PersistenceSuite() {
	s := new(persistencetests.HistoryV2PersistenceSuite)
	s.TestBase = newSQLServerTestBase()
	s.Setup(nil)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerMetadataPersistenceSuiteV2() {
	s := new(persistencetests.MetadataPersistenceSuiteV2)
	s.TestBase = newSQLServerTestBase()
	s.Setup(nil)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerClusterMetadataPersistence() {
	s := new(persistencetests.ClusterMetadataManagerSuite)
	s.TestBase = newSQLServerTestBase()
	s.Setup(nil)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerQueuePersistence() {
	s := new(persistencetests.QueuePersistenceSuite)
	s.TestBase = newSQLServerTestBase()
	s.Setup(nil)
	suite.Run(p.T(), s)
}

// SQL store tests

func (p *SQLServerSuite) TestSQLServerNamespaceSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewNamespaceSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerQueueMessageSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewQueueMessageSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerQueueMetadataSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewQueueMetadataSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerMatchingTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewMatchingTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerMatchingTaskV2Suite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewMatchingTaskV2Suite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerMatchingTaskQueueSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewMatchingTaskQueueSuite(p.T(), store, sqlplugin.MatchingTaskVersion1)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerMatchingFairTaskQueueSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewMatchingTaskQueueSuite(p.T(), store, sqlplugin.MatchingTaskVersion2)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryShardSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryShardSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryNodeSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryNodeSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryTreeSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryTreeSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryCurrentExecutionSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryCurrentExecutionSuite(p.T(), store, chasm.WorkflowArchetypeID)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryCurrentChasmExecutionSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryCurrentExecutionSuite(p.T(), store, math.MaxUint32)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryTransferTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryTransferTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryTimerTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryTimerTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryReplicationTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryReplicationTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryVisibilityTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryVisibilityTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryReplicationDLQTaskSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryReplicationDLQTaskSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionBufferSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionBufferSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionActivitySuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionActivitySuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionChildWorkflowSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionChildWorkflowSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionTimerSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionTimerSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionChasmSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)

	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionChasmSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionRequestCancelSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionRequestCancelSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionSignalSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionSignalSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerHistoryExecutionSignalRequestSuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindMain, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewHistoryExecutionSignalRequestSuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerVisibilitySuite() {
	cfg := NewSQLServerConfig()
	SetupSQLServerDatabase(p.T(), cfg)
	SetupSQLServerSchema(p.T(), cfg)
	store, err := sql.NewSQLDB(sqlplugin.DbKindVisibility, cfg, resolver.NewNoopResolver(), log.NewTestLogger(), metrics.NoopMetricsHandler)
	if err != nil {
		p.T().Fatalf("unable to create SQL Server DB: %v", err)
	}
	defer func() {
		_ = store.Close()
		TearDownSQLServerDatabase(p.T(), cfg)
	}()

	s := sqltests.NewVisibilitySuite(p.T(), store)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerClosedConnectionError() {
	testData, tearDown := setUpSQLServerTest(p.T())
	defer tearDown()

	s := newConnectionSuite(p.T(), testData.Factory)
	suite.Run(p.T(), s)
}

func (p *SQLServerSuite) TestSQLServerTestClusterDropsDatabaseWithOpenConnection() {
	cfg := asAdmin(NewSQLServerConfig())
	SetupSQLServerDatabase(p.T(), cfg)

	db, err := sql.NewSQLAdminDB(
		sqlplugin.DbKindUnknown,
		cfg,
		resolver.NewNoopResolver(),
		log.NewTestLogger(),
		metrics.NoopMetricsHandler,
	)
	p.Require().NoError(err)
	p.T().Cleanup(func() {
		p.Require().NoError(db.Close())
		TearDownSQLServerDatabase(p.T(), cfg)
	})

	testCluster := sql.NewTestCluster(
		cfg.PluginName,
		cfg.DatabaseName,
		cfg.User,
		cfg.Password,
		sqlServerHost(),
		sqlServerPort(),
		cfg.ConnectAttributes,
		"",
		nil,
		log.NewTestLogger(),
	)
	testCluster.DropDatabase()
}

func (p *SQLServerSuite) TestSQLServerQueueV2() {
	testData, tearDown := setUpSQLServerTest(p.T())
	p.T().Cleanup(tearDown)
	ptests.RunQueueV2TestSuiteForSQL(p.T(), testData.Factory)
}

func (p *SQLServerSuite) TestSQLServerNexusEndpointPersistence() {
	testData, tearDown := setUpSQLServerTest(p.T())
	p.T().Cleanup(tearDown)
	ptests.RunNexusEndpointTestSuiteForSQL(p.T(), testData.Factory)
}

func TestSQLServer(t *testing.T) {
	requireSQLServer(t)
	suite.Run(t, &SQLServerSuite{})
}
