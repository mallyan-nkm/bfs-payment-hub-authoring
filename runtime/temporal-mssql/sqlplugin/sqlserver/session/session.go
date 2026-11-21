// Package session opens the connection pool of the SQL Server plugin.
package session

import (
	"database/sql"
	sqldriver "database/sql/driver"
	"fmt"
	"net"
	"net/url"
	"strings"

	"github.com/iancoleman/strcase"
	"github.com/jmoiron/sqlx"
	mssql "github.com/microsoft/go-mssqldb"
	"go.temporal.io/server/common/config"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/resolver"
)

// DriverName is the database/sql driver name of go-mssqldb that takes @Name and @pN parameters.
// sqlx binds named queries for it as @p1, @p2, ...
const DriverName = "sqlserver"

const (
	attrEncrypt         = "encrypt"
	attrCertificate     = "certificate"
	attrHostNameInCert  = "hostnameincertificate"
	attrTrustServerCert = "trustservercertificate"
	attrAppName         = "app name"
)

// NewSession opens a connection pool to the database cfg names.
func NewSession(cfg *config.SQL, r resolver.ServiceResolver) (*sqlx.DB, error) {
	var db *sqlx.DB
	if cfg.PasswordCommand != nil {
		c := sqlplugin.NewRefreshingConnector(
			func() (string, error) { return BuildDSN(cfg, r) },
			func(dsn string) (sqldriver.Connector, error) { return mssql.NewConnector(dsn) },
			&mssql.Driver{},
		)
		db = sqlx.NewDb(sql.OpenDB(c), DriverName)
		if err := db.Ping(); err != nil {
			_ = db.Close()
			return nil, err
		}
	} else {
		dsn, err := BuildDSN(cfg, r)
		if err != nil {
			return nil, err
		}
		db, err = sqlx.Connect(DriverName, dsn)
		if err != nil {
			return nil, err
		}
	}
	if cfg.MaxConns > 0 {
		db.SetMaxOpenConns(cfg.MaxConns)
	}
	if cfg.MaxIdleConns > 0 {
		db.SetMaxIdleConns(cfg.MaxIdleConns)
	}
	if cfg.MaxConnLifetime > 0 {
		db.SetConnMaxLifetime(cfg.MaxConnLifetime)
	}
	// Temporal's row structs have no db tags: ShardID maps to shard_id.
	db.MapperFunc(strcase.ToSnake)
	return db, nil
}

// BuildDSN builds a go-mssqldb URL: sqlserver://user:password@host:port?database=name&...
// connectAttributes are passed through as URL parameters (for example encrypt, app name,
// connection timeout).
func BuildDSN(cfg *config.SQL, r resolver.ServiceResolver) (string, error) {
	password, err := cfg.ResolvePassword()
	if err != nil {
		return "", err
	}
	addr := r.Resolve(cfg.ConnectAddr)[0]
	if _, _, err := net.SplitHostPort(addr); err != nil {
		return "", fmt.Errorf("sqlserver: connectAddr %q must be host:port: %w", addr, err)
	}
	params := url.Values{}
	for k, v := range cfg.ConnectAttributes {
		key := strings.ToLower(strings.TrimSpace(k))
		if params.Get(key) != "" {
			return "", fmt.Errorf("sqlserver: duplicate connect attribute %q", key)
		}
		params.Set(key, strings.TrimSpace(v))
	}
	params.Set("database", cfg.DatabaseName)
	if params.Get(attrAppName) == "" {
		params.Set(attrAppName, "temporal")
	}
	if cfg.TLS != nil && cfg.TLS.Enabled {
		if params.Get(attrEncrypt) == "" {
			params.Set(attrEncrypt, "true")
		}
		if cfg.TLS.CaFile != "" && params.Get(attrCertificate) == "" {
			params.Set(attrCertificate, cfg.TLS.CaFile)
		}
		if cfg.TLS.ServerName != "" && params.Get(attrHostNameInCert) == "" {
			params.Set(attrHostNameInCert, cfg.TLS.ServerName)
		}
		if !cfg.TLS.EnableHostVerification && params.Get(attrTrustServerCert) == "" {
			params.Set(attrTrustServerCert, "true")
		}
		if cfg.TLS.CertFile != "" || cfg.TLS.KeyFile != "" {
			return "", fmt.Errorf("sqlserver: client certificates are not supported; use SQL or Entra ID authentication")
		}
	}
	u := &url.URL{
		Scheme:   "sqlserver",
		User:     url.UserPassword(cfg.User, password),
		Host:     addr,
		RawQuery: params.Encode(),
	}
	return u.String(), nil
}
