package sqlserver

import (
	"context"
	"database/sql"
	"fmt"
	"strings"
	"time"

	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

var (
	// The values are bound where they are used (not selected into the MERGE source), so that a NULL
	// parameter converts to the column's type.
	visibilityMergeHead = `MERGE INTO executions_visibility WITH (HOLDLOCK) AS t
		USING (SELECT CAST(:namespace_id AS CHAR(64)) AS namespace_id, CAST(:run_id AS CHAR(64)) AS run_id, CAST(:_version AS BIGINT) AS _version) AS s
		ON t.namespace_id = s.namespace_id AND t.run_id = s.run_id `

	visibilityMergeInsert = fmt.Sprintf(
		`WHEN NOT MATCHED THEN INSERT (%s) VALUES (%s);`,
		strings.Join(sqlplugin.DbFields, ", "),
		sqlplugin.BuildNamedPlaceholder(sqlplugin.DbFields...),
	)

	templateInsertWorkflowExecution = visibilityMergeHead + visibilityMergeInsert

	templateUpsertWorkflowExecution = visibilityMergeHead + buildOnDuplicateKeyUpdate(sqlplugin.DbFields...) + visibilityMergeInsert

	templateDeleteWorkflowExecution_v12 = `
		DELETE FROM executions_visibility
		WHERE namespace_id = :namespace_id AND run_id = :run_id`

	templateGetWorkflowExecution_v12 = fmt.Sprintf(
		`SELECT %s FROM executions_visibility
		WHERE namespace_id = @p1 AND run_id = @p2`,
		strings.Join(sqlplugin.DbFields, ", "),
	)
)

func buildOnDuplicateKeyUpdate(fields ...string) string {
	items := make([]string, 0, len(fields))
	for _, field := range fields {
		if field == "namespace_id" || field == "run_id" {
			continue
		}
		items = append(items, fmt.Sprintf("%s = :%s", field, field))
	}
	return fmt.Sprintf(
		// The version condition ensures that no update occurs if the version is behind the saved version.
		"WHEN MATCHED AND t.%s < s.%s THEN UPDATE SET %s ",
		sqlplugin.VersionColumnName, sqlplugin.VersionColumnName, strings.Join(items, ", "),
	)
}

// InsertIntoVisibility inserts a row into visibility table. If an row already exist,
// its left as such and no update will be made
func (pdb *db) InsertIntoVisibility(
	ctx context.Context,
	row *sqlplugin.VisibilityRow,
) (sql.Result, error) {
	finalRow := pdb.prepareRowForDB(row)
	return pdb.NamedExecContext(ctx, templateInsertWorkflowExecution, finalRow)
}

// ReplaceIntoVisibility replaces an existing row if it exist or creates a new row in visibility table
func (pdb *db) ReplaceIntoVisibility(
	ctx context.Context,
	row *sqlplugin.VisibilityRow,
) (sql.Result, error) {
	finalRow := pdb.prepareRowForDB(row)
	return pdb.NamedExecContext(ctx, templateUpsertWorkflowExecution, finalRow)
}

// DeleteFromVisibility deletes a row from visibility table if it exist
func (pdb *db) DeleteFromVisibility(
	ctx context.Context,
	filter sqlplugin.VisibilityDeleteFilter,
) (sql.Result, error) {
	return pdb.NamedExecContext(ctx, templateDeleteWorkflowExecution_v12, filter)
}

// SelectFromVisibility reads one or more rows from visibility table
func (pdb *db) SelectFromVisibility(
	ctx context.Context,
	filter sqlplugin.VisibilitySelectFilter,
) ([]sqlplugin.VisibilityRow, error) {
	if len(filter.Query) == 0 {
		// backward compatibility for existing tests
		err := sqlplugin.GenerateSelectQuery(&filter, pdb.converter.ToSQLServerDateTime)
		if err == nil {
			filter.Query = rewriteLegacyLimit(filter.Query)
		}
		if err != nil {
			return nil, err
		}
	}

	// Rebind will replace default placeholder `?` with the right placeholder for SQL Server (@pN).
	db, err := pdb.handle.DB()
	if err != nil {
		return nil, err
	}
	filter.Query = db.Rebind(filter.Query)
	var rows []sqlplugin.VisibilityRow
	err = db.SelectContext(ctx, &rows, filter.Query, convertArgs(filter.QueryArgs)...)
	if err != nil {
		return nil, err
	}
	for i := range rows {
		err = pdb.processRowFromDB(&rows[i])
		if err != nil {
			return nil, err
		}
	}
	return rows, nil
}

// GetFromVisibility reads one row from visibility table
func (pdb *db) GetFromVisibility(
	ctx context.Context,
	filter sqlplugin.VisibilityGetFilter,
) (*sqlplugin.VisibilityRow, error) {
	var row sqlplugin.VisibilityRow
	if err := pdb.GetContext(
		ctx,
		&row,
		templateGetWorkflowExecution_v12,
		filter.NamespaceID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	if err := pdb.processRowFromDB(&row); err != nil {
		return nil, err
	}
	return &row, nil
}

func (pdb *db) CountFromVisibility(
	ctx context.Context,
	filter sqlplugin.VisibilitySelectFilter,
) (int64, error) {
	var count int64
	filter.Query = pdb.Rebind(filter.Query)
	err := pdb.GetContext(ctx, &count, filter.Query, filter.QueryArgs...)
	if err != nil {
		return 0, err
	}
	return count, nil
}

func (pdb *db) CountGroupByFromVisibility(
	ctx context.Context,
	filter sqlplugin.VisibilitySelectFilter,
) ([]sqlplugin.VisibilityCountRow, error) {
	filter.Query = pdb.Rebind(filter.Query)
	rows, err := pdb.QueryContext(ctx, filter.Query, filter.QueryArgs...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return sqlplugin.ParseCountGroupByRows(rows, filter.GroupBy)
}

func (pdb *db) prepareRowForDB(row *sqlplugin.VisibilityRow) *sqlplugin.VisibilityRow {
	if row == nil {
		return nil
	}
	finalRow := *row
	finalRow.StartTime = pdb.converter.ToSQLServerDateTime(finalRow.StartTime)
	finalRow.ExecutionTime = pdb.converter.ToSQLServerDateTime(finalRow.ExecutionTime)
	if finalRow.CloseTime != nil {
		*finalRow.CloseTime = pdb.converter.ToSQLServerDateTime(*finalRow.CloseTime)
	}
	if finalRow.SearchAttributes != nil {
		saMap := *finalRow.SearchAttributes
		for name, value := range saMap {
			if dt, ok := value.(time.Time); ok {
				// UTC, so that the computed DATETIME2 columns can read it (see the visibility schema).
				saMap[name] = dt.UTC().Format(time.RFC3339Nano)
			}
		}
	}
	return &finalRow
}

func (pdb *db) processRowFromDB(row *sqlplugin.VisibilityRow) error {
	if row == nil {
		return nil
	}
	row.StartTime = pdb.converter.FromSQLServerDateTime(row.StartTime)
	row.ExecutionTime = pdb.converter.FromSQLServerDateTime(row.ExecutionTime)
	if row.CloseTime != nil {
		closeTime := pdb.converter.FromSQLServerDateTime(*row.CloseTime)
		row.CloseTime = &closeTime
	}
	if row.SearchAttributes != nil {
		for saName, saValue := range *row.SearchAttributes {
			switch typedSaValue := saValue.(type) {
			case []any:
				// the only valid type is slice of strings
				strSlice := make([]string, len(typedSaValue))
				for i, item := range typedSaValue {
					switch v := item.(type) {
					case string:
						strSlice[i] = v
					default:
						return fmt.Errorf("%w: %T (expected string)", sqlplugin.ErrInvalidKeywordListDataType, v)
					}
				}
				(*row.SearchAttributes)[saName] = strSlice
			default:
				// no-op
			}
		}
	}
	// need to trim the run ID, or otherwise the returned value will
	// come with lots of trailing spaces, probably due to the CHAR(64) type
	row.RunID = strings.TrimSpace(row.RunID)
	return nil
}

// rewriteLegacyLimit turns the "LIMIT ?" that sqlplugin.GenerateSelectQuery writes into T-SQL.
func rewriteLegacyLimit(q string) string {
	if i := strings.LastIndex(q, "LIMIT ?"); i >= 0 {
		return q[:i] + "OFFSET 0 ROWS FETCH NEXT ? ROWS ONLY" + q[i+len("LIMIT ?"):]
	}
	return q
}
