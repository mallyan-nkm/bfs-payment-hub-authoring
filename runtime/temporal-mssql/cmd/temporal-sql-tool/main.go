// temporal-sql-tool with the SQL Server plugin registered: create-database, setup-schema and
// update-schema work on SQL Server with --plugin sqlserver. This is cmd/tools/sql/main.go of
// go.temporal.io/server v1.32.2 (MIT licence, Temporal Technologies), unchanged but for the import.

package main

import (
	"os"

	_ "github.com/transacthub/temporal-mssql/sqlplugin/sqlserver"         // the SQL Server plugin
	_ "go.temporal.io/server/common/persistence/sql/sqlplugin/mysql"      // needed to load mysql plugin
	_ "go.temporal.io/server/common/persistence/sql/sqlplugin/postgresql" // needed to load postgresql plugin
	_ "go.temporal.io/server/common/persistence/sql/sqlplugin/sqlite"     // needed to load sqlite plugin
	"go.temporal.io/server/tools/sql"
)

func main() {
	if err := sql.RunTool(os.Args); err != nil {
		os.Exit(1)
	}
}
