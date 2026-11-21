// Package sqlserver is a Temporal persistence plugin for Microsoft SQL Server 2022 (and Azure SQL).
// It is a port of Temporal's PostgreSQL plugin (go.temporal.io/server v1.32.2,
// common/persistence/sql/sqlplugin/postgresql) to T-SQL, on github.com/microsoft/go-mssqldb.
//
// Import it for its side effect: it registers the plugin as "sqlserver" with Temporal's SQL store.
//
//	import _ "github.com/transacthub/temporal-mssql/sqlplugin/sqlserver"
package sqlserver

import (
	"github.com/jmoiron/sqlx"
	"go.temporal.io/server/common/clock"
	"go.temporal.io/server/common/config"
	"go.temporal.io/server/common/log"
	"go.temporal.io/server/common/metrics"
	"go.temporal.io/server/common/persistence/sql"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/resolver"

	"github.com/transacthub/temporal-mssql/sqlplugin/sqlserver/session"
)

const (
	// PluginName is the name the plugin registers under: `pluginName: "sqlserver"` in the server's
	// persistence configuration, `--plugin sqlserver` for temporal-sql-tool.
	PluginName = "sqlserver"

	// Version is the persistence schema version this plugin expects. It follows Temporal's own
	// numbering for MySQL and PostgreSQL (schema/postgresql/v12/version.go at v1.32.2).
	Version = "1.19"

	// VisibilityVersion is the visibility schema version this plugin expects.
	VisibilityVersion = "1.14"

	// defaultDatabaseName is the database the plugin connects to when the configuration names none
	// (to create or drop a database).
	defaultDatabaseName = "master"
)

type plugin struct {
	queryConverter sqlplugin.VisibilityQueryConverter
}

var _ sqlplugin.Plugin = (*plugin)(nil)

func init() {
	sql.RegisterPlugin(PluginName, &plugin{queryConverter: &queryConverter{}})
}

func (p *plugin) GetVisibilityQueryConverter() sqlplugin.VisibilityQueryConverter {
	return p.queryConverter
}

// CreateDB initialises the db object.
func (p *plugin) CreateDB(
	dbKind sqlplugin.DbKind,
	cfg *config.SQL,
	r resolver.ServiceResolver,
	logger log.Logger,
	metricsHandler metrics.Handler,
) (sqlplugin.GenericDB, error) {
	connect := func() (*sqlx.DB, error) {
		if cfg.Connect != nil {
			return cfg.Connect(cfg)
		}
		return p.createDBConnection(cfg, r)
	}
	handle := sqlplugin.NewDatabaseHandle(dbKind, connect, isConnNeedsRefreshError, logger, metricsHandler, clock.NewRealTimeSource())
	return newDB(dbKind, cfg.DatabaseName, handle, nil, logger), nil
}

func (p *plugin) createDBConnection(cfg *config.SQL, r resolver.ServiceResolver) (*sqlx.DB, error) {
	if cfg.DatabaseName != "" {
		return session.NewSession(cfg, r)
	}
	// No database named: connect to master, to create or drop one. A shallow copy, so that a
	// refreshing connection (which keeps the config) sees the same name on reconnect.
	cfgCopy := *cfg
	cfgCopy.DatabaseName = defaultDatabaseName
	return session.NewSession(&cfgCopy, r)
}
