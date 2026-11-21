package sqlserver

import (
	"context"
	"database/sql"

	"go.temporal.io/api/serviceerror"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const (
	getTaskMinMaxQry = `SELECT task_id, data, data_encoding ` +
		`FROM tasks ` +
		`WHERE range_hash = @p1 AND task_queue_id=@p2 AND task_id >= @p3 AND task_id < @p4 ` +
		`ORDER BY task_id OFFSET 0 ROWS FETCH NEXT @p5 ROWS ONLY`

	getTaskMinQry = `SELECT task_id, data, data_encoding ` +
		`FROM tasks ` +
		`WHERE range_hash = @p1 AND task_queue_id = @p2 AND task_id >= @p3 ORDER BY task_id OFFSET 0 ROWS FETCH NEXT @p4 ROWS ONLY`

	createTaskQry = `INSERT INTO ` +
		`tasks(range_hash, task_queue_id, task_id, data, data_encoding) ` +
		`VALUES(:range_hash, :task_queue_id, :task_id, :data, :data_encoding)`

	rangeDeleteTaskQry = `DELETE FROM tasks ` +
		`WHERE range_hash = @p1 AND task_queue_id = @p2 AND task_id IN (SELECT task_id FROM
		 tasks WHERE range_hash = @p1 AND task_queue_id = @p2 AND task_id < @p3 ` +
		`ORDER BY task_queue_id,task_id OFFSET 0 ROWS FETCH NEXT @p4 ROWS ONLY )`
)

// InsertIntoTasks inserts one or more rows into tasks table
func (pdb *db) InsertIntoTasks(
	ctx context.Context,
	rows []sqlplugin.TasksRow,
) (sql.Result, error) {
	return pdb.NamedExecContext(ctx,
		createTaskQry,
		rows,
	)
}

// SelectFromTasks reads one or more rows from tasks table
func (pdb *db) SelectFromTasks(
	ctx context.Context,
	filter sqlplugin.TasksFilter,
) ([]sqlplugin.TasksRow, error) {
	var err error
	var rows []sqlplugin.TasksRow
	switch {
	case filter.ExclusiveMaxTaskID != nil:
		err = pdb.SelectContext(ctx,
			&rows,
			getTaskMinMaxQry,
			filter.RangeHash,
			filter.TaskQueueID,
			*filter.InclusiveMinTaskID,
			*filter.ExclusiveMaxTaskID,
			*filter.PageSize,
		)
	default:
		err = pdb.SelectContext(ctx,
			&rows,
			getTaskMinQry,
			filter.RangeHash,
			filter.TaskQueueID,
			*filter.InclusiveMinTaskID,
			*filter.PageSize,
		)
	}
	return rows, err
}

// DeleteFromTasks deletes multiple rows from tasks table
func (pdb *db) DeleteFromTasks(
	ctx context.Context,
	filter sqlplugin.TasksFilter,
) (sql.Result, error) {
	if filter.ExclusiveMaxTaskID == nil {
		return nil, serviceerror.NewInternal("missing ExclusiveMaxTaskID parameter")
	}
	if filter.Limit == nil || *filter.Limit == 0 {
		return nil, serviceerror.NewInternal("missing limit parameter")
	}
	return pdb.ExecContext(ctx,
		rangeDeleteTaskQry,
		filter.RangeHash,
		filter.TaskQueueID,
		*filter.ExclusiveMaxTaskID,
		*filter.Limit,
	)
}
