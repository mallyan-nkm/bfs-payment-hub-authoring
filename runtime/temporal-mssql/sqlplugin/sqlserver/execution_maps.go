package sqlserver

import (
	"context"
	"database/sql"
	"fmt"
	"strings"

	"github.com/jmoiron/sqlx"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const (
	deleteMapQueryTemplate = `DELETE FROM %v
WHERE
shard_id = @p1 AND
namespace_id = @p2 AND
workflow_id = @p3 AND
run_id = @p4`

	// %[2]v is the columns of the value struct (i.e. no primary key columns), comma separated
	// %[3]v is the source select list: ":col AS col" for each of %[2]v
	// %[4]v is the name of the key associated with the map, e.g. "schedule_id" for ActivityInfo
	// %[5]v is the update list: "col = s.col" for each of %[2]v
	// %[6]v is %[2]v with "s." prepended
	// %[7]v is the source expression of the map key
	// The upsert is one row; a batch runs row by row in the caller's transaction (see execUpsert).
	setKeyInMapQueryTemplate = `MERGE INTO %[1]v WITH (HOLDLOCK) AS t
USING (SELECT :shard_id AS shard_id, :namespace_id AS namespace_id, CAST(:workflow_id AS VARCHAR(255)) AS workflow_id, :run_id AS run_id, %[7]v AS %[4]v, %[3]v) AS s
ON t.shard_id = s.shard_id AND t.namespace_id = s.namespace_id AND t.workflow_id = s.workflow_id AND t.run_id = s.run_id AND t.%[4]v = s.%[4]v
WHEN MATCHED THEN UPDATE SET %[5]v
WHEN NOT MATCHED THEN INSERT (shard_id, namespace_id, workflow_id, run_id, %[4]v, %[2]v)
VALUES (s.shard_id, s.namespace_id, s.workflow_id, s.run_id, s.%[4]v, %[6]v);`

	// %[2]v is the name of the key
	// NOTE: sqlx only support ? when doing `sqlx.In` expanding query
	deleteKeyInMapQueryTemplate = `DELETE FROM %[1]v
WHERE
shard_id = ? AND
namespace_id = ? AND
workflow_id = ? AND
run_id = ? AND
%[2]v IN ( ? )`

	// %[1]v is the name of the table
	// %[2]v is the name of the key
	// %[3]v is the value columns, separated by commas
	getMapQueryTemplate = `SELECT %[2]v, %[3]v FROM %[1]v
WHERE
shard_id = @p1 AND
namespace_id = @p2 AND
workflow_id = @p3 AND
run_id = @p4`
)

const (
	deleteAllSignalsRequestedSetQuery = `DELETE FROM signals_requested_sets
WHERE
shard_id = @p1 AND
namespace_id = @p2 AND
workflow_id = @p3 AND
run_id = @p4
`

	createSignalsRequestedSetQuery = `MERGE INTO signals_requested_sets WITH (HOLDLOCK) AS t
USING (SELECT :shard_id AS shard_id, :namespace_id AS namespace_id, CAST(:workflow_id AS VARCHAR(255)) AS workflow_id, :run_id AS run_id, CAST(:signal_id AS VARCHAR(255)) AS signal_id) AS s
ON t.shard_id = s.shard_id AND t.namespace_id = s.namespace_id AND t.workflow_id = s.workflow_id AND t.run_id = s.run_id AND t.signal_id = s.signal_id
WHEN NOT MATCHED THEN INSERT (shard_id, namespace_id, workflow_id, run_id, signal_id)
VALUES (s.shard_id, s.namespace_id, s.workflow_id, s.run_id, s.signal_id);`

	// NOTE: sqlx only support ? when doing `sqlx.In` expanding query
	deleteSignalsRequestedSetQuery = `DELETE FROM signals_requested_sets
WHERE
shard_id = ? AND
namespace_id = ? AND
workflow_id = ? AND
run_id = ? AND
signal_id IN ( ? )`

	getSignalsRequestedSetQuery = `SELECT signal_id FROM signals_requested_sets WHERE
shard_id = @p1 AND
namespace_id = @p2 AND
workflow_id = @p3 AND
run_id = @p4`
)

func stringMap(a []string, f func(string) string) []string {
	b := make([]string, len(a))
	for i, v := range a {
		b[i] = f(v)
	}
	return b
}

func makeDeleteMapQry(tableName string) string {
	return fmt.Sprintf(deleteMapQueryTemplate, tableName)
}

func makeSetKeyInMapQry(tableName string, nonPrimaryKeyColumns []string, mapKeyName string) string {
	return makeSetKeyInMapQryWithKeySource(tableName, nonPrimaryKeyColumns, mapKeyName, ":"+mapKeyName)
}

func makeSetKeyInMapQryWithKeySource(tableName string, nonPrimaryKeyColumns []string, mapKeyName string, keySource string) string {
	return fmt.Sprintf(setKeyInMapQueryTemplate,
		tableName,
		strings.Join(nonPrimaryKeyColumns, ","),
		strings.Join(stringMap(nonPrimaryKeyColumns, func(x string) string {
			return ":" + x + " AS " + x
		}), ","),
		mapKeyName,
		strings.Join(stringMap(nonPrimaryKeyColumns, func(x string) string {
			return x + " = s." + x
		}), ","),
		strings.Join(stringMap(nonPrimaryKeyColumns, func(x string) string {
			return "s." + x
		}), ","),
		keySource)
}

func makeDeleteKeyInMapQry(tableName string, mapKeyName string) string {
	return fmt.Sprintf(deleteKeyInMapQueryTemplate,
		tableName,
		mapKeyName)
}

func makeGetMapQryTemplate(tableName string, nonPrimaryKeyColumns []string, mapKeyName string) string {
	return fmt.Sprintf(getMapQueryTemplate,
		tableName,
		mapKeyName,
		strings.Join(nonPrimaryKeyColumns, ","))
}

var (
	// Omit shard_id, run_id, namespace_id, workflow_id, schedule_id since they're in the primary key
	activityInfoColumns = []string{
		"data",
		"data_encoding",
	}
	activityInfoTableName = "activity_info_maps"
	activityInfoKey       = "schedule_id"

	deleteActivityInfoMapQry      = makeDeleteMapQry(activityInfoTableName)
	setKeyInActivityInfoMapQry    = makeSetKeyInMapQry(activityInfoTableName, activityInfoColumns, activityInfoKey)
	deleteKeyInActivityInfoMapQry = makeDeleteKeyInMapQry(activityInfoTableName, activityInfoKey)
	getActivityInfoMapQry         = makeGetMapQryTemplate(activityInfoTableName, activityInfoColumns, activityInfoKey)
)

// ReplaceIntoActivityInfoMaps replaces one or more rows in activity_info_maps table
func (pdb *db) ReplaceIntoActivityInfoMaps(
	ctx context.Context,
	rows []sqlplugin.ActivityInfoMapsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		setKeyInActivityInfoMapQry,
		rows,
	)
}

// SelectAllFromActivityInfoMaps reads all rows from activity_info_maps table
func (pdb *db) SelectAllFromActivityInfoMaps(
	ctx context.Context,
	filter sqlplugin.ActivityInfoMapsAllFilter,
) ([]sqlplugin.ActivityInfoMapsRow, error) {
	var rows []sqlplugin.ActivityInfoMapsRow
	if err := pdb.SelectContext(ctx,
		&rows, getActivityInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromActivityInfoMaps deletes one or more rows from activity_info_maps table
func (pdb *db) DeleteFromActivityInfoMaps(
	ctx context.Context,
	filter sqlplugin.ActivityInfoMapsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInActivityInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.ScheduleIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromActivityInfoMaps deletes all rows from activity_info_maps table
func (pdb *db) DeleteAllFromActivityInfoMaps(
	ctx context.Context,
	filter sqlplugin.ActivityInfoMapsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteActivityInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

var (
	timerInfoColumns = []string{
		"data",
		"data_encoding",
	}
	timerInfoTableName = "timer_info_maps"
	timerInfoKey       = "timer_id"

	deleteTimerInfoMapSQLQuery      = makeDeleteMapQry(timerInfoTableName)
	setKeyInTimerInfoMapSQLQuery    = makeSetKeyInMapQry(timerInfoTableName, timerInfoColumns, timerInfoKey)
	deleteKeyInTimerInfoMapSQLQuery = makeDeleteKeyInMapQry(timerInfoTableName, timerInfoKey)
	getTimerInfoMapSQLQuery         = makeGetMapQryTemplate(timerInfoTableName, timerInfoColumns, timerInfoKey)
)

// ReplaceIntoTimerInfoMaps replaces one or more rows in timer_info_maps table
func (pdb *db) ReplaceIntoTimerInfoMaps(
	ctx context.Context,
	rows []sqlplugin.TimerInfoMapsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		setKeyInTimerInfoMapSQLQuery,
		rows,
	)
}

// SelectAllFromTimerInfoMaps reads all rows from timer_info_maps table
func (pdb *db) SelectAllFromTimerInfoMaps(
	ctx context.Context,
	filter sqlplugin.TimerInfoMapsAllFilter,
) ([]sqlplugin.TimerInfoMapsRow, error) {
	var rows []sqlplugin.TimerInfoMapsRow
	if err := pdb.SelectContext(ctx,
		&rows,
		getTimerInfoMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromTimerInfoMaps deletes one or more rows from timer_info_maps table
func (pdb *db) DeleteFromTimerInfoMaps(
	ctx context.Context,
	filter sqlplugin.TimerInfoMapsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInTimerInfoMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.TimerIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromTimerInfoMaps deletes all rows from timer_info_maps table
func (pdb *db) DeleteAllFromTimerInfoMaps(
	ctx context.Context,
	filter sqlplugin.TimerInfoMapsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteTimerInfoMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

var (
	childExecutionInfoColumns = []string{
		"data",
		"data_encoding",
	}
	childExecutionInfoTableName = "child_execution_info_maps"
	childExecutionInfoKey       = "initiated_id"

	deleteChildExecutionInfoMapQry      = makeDeleteMapQry(childExecutionInfoTableName)
	setKeyInChildExecutionInfoMapQry    = makeSetKeyInMapQry(childExecutionInfoTableName, childExecutionInfoColumns, childExecutionInfoKey)
	deleteKeyInChildExecutionInfoMapQry = makeDeleteKeyInMapQry(childExecutionInfoTableName, childExecutionInfoKey)
	getChildExecutionInfoMapQry         = makeGetMapQryTemplate(childExecutionInfoTableName, childExecutionInfoColumns, childExecutionInfoKey)
)

// ReplaceIntoChildExecutionInfoMaps replaces one or more rows in child_execution_info_maps table
func (pdb *db) ReplaceIntoChildExecutionInfoMaps(
	ctx context.Context,
	rows []sqlplugin.ChildExecutionInfoMapsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		setKeyInChildExecutionInfoMapQry,
		rows,
	)
}

// SelectAllFromChildExecutionInfoMaps reads all rows from child_execution_info_maps table
func (pdb *db) SelectAllFromChildExecutionInfoMaps(
	ctx context.Context,
	filter sqlplugin.ChildExecutionInfoMapsAllFilter,
) ([]sqlplugin.ChildExecutionInfoMapsRow, error) {
	var rows []sqlplugin.ChildExecutionInfoMapsRow
	if err := pdb.SelectContext(ctx,
		&rows,
		getChildExecutionInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromChildExecutionInfoMaps deletes one or more rows from child_execution_info_maps table
func (pdb *db) DeleteFromChildExecutionInfoMaps(
	ctx context.Context,
	filter sqlplugin.ChildExecutionInfoMapsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInChildExecutionInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.InitiatedIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromChildExecutionInfoMaps deletes all rows from child_execution_info_maps table
func (pdb *db) DeleteAllFromChildExecutionInfoMaps(
	ctx context.Context,
	filter sqlplugin.ChildExecutionInfoMapsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteChildExecutionInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

var (
	requestCancelInfoColumns = []string{
		"data",
		"data_encoding",
	}
	requestCancelInfoTableName = "request_cancel_info_maps"
	requestCancelInfoKey       = "initiated_id"

	deleteRequestCancelInfoMapQry      = makeDeleteMapQry(requestCancelInfoTableName)
	setKeyInRequestCancelInfoMapQry    = makeSetKeyInMapQry(requestCancelInfoTableName, requestCancelInfoColumns, requestCancelInfoKey)
	deleteKeyInRequestCancelInfoMapQry = makeDeleteKeyInMapQry(requestCancelInfoTableName, requestCancelInfoKey)
	getRequestCancelInfoMapQry         = makeGetMapQryTemplate(requestCancelInfoTableName, requestCancelInfoColumns, requestCancelInfoKey)
)

// ReplaceIntoRequestCancelInfoMaps replaces one or more rows in request_cancel_info_maps table
func (pdb *db) ReplaceIntoRequestCancelInfoMaps(
	ctx context.Context,
	rows []sqlplugin.RequestCancelInfoMapsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		setKeyInRequestCancelInfoMapQry,
		rows,
	)
}

// SelectAllFromRequestCancelInfoMaps reads all rows from request_cancel_info_maps table
func (pdb *db) SelectAllFromRequestCancelInfoMaps(
	ctx context.Context,
	filter sqlplugin.RequestCancelInfoMapsAllFilter,
) ([]sqlplugin.RequestCancelInfoMapsRow, error) {
	var rows []sqlplugin.RequestCancelInfoMapsRow
	if err := pdb.SelectContext(ctx,
		&rows,
		getRequestCancelInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromRequestCancelInfoMaps deletes one or more rows from request_cancel_info_maps table
func (pdb *db) DeleteFromRequestCancelInfoMaps(
	ctx context.Context,
	filter sqlplugin.RequestCancelInfoMapsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInRequestCancelInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.InitiatedIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromRequestCancelInfoMaps deletes all rows from request_cancel_info_maps table
func (pdb *db) DeleteAllFromRequestCancelInfoMaps(
	ctx context.Context,
	filter sqlplugin.RequestCancelInfoMapsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteRequestCancelInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

var (
	signalInfoColumns = []string{
		"data",
		"data_encoding",
	}
	signalInfoTableName = "signal_info_maps"
	signalInfoKey       = "initiated_id"

	deleteSignalInfoMapQry      = makeDeleteMapQry(signalInfoTableName)
	setKeyInSignalInfoMapQry    = makeSetKeyInMapQry(signalInfoTableName, signalInfoColumns, signalInfoKey)
	deleteKeyInSignalInfoMapQry = makeDeleteKeyInMapQry(signalInfoTableName, signalInfoKey)
	getSignalInfoMapQry         = makeGetMapQryTemplate(signalInfoTableName, signalInfoColumns, signalInfoKey)
)

// ReplaceIntoSignalInfoMaps replaces one or more rows in signal_info_maps table
func (pdb *db) ReplaceIntoSignalInfoMaps(
	ctx context.Context,
	rows []sqlplugin.SignalInfoMapsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		setKeyInSignalInfoMapQry,
		rows,
	)
}

// SelectAllFromSignalInfoMaps reads all rows from signal_info_maps table
func (pdb *db) SelectAllFromSignalInfoMaps(
	ctx context.Context,
	filter sqlplugin.SignalInfoMapsAllFilter,
) ([]sqlplugin.SignalInfoMapsRow, error) {
	var rows []sqlplugin.SignalInfoMapsRow
	if err := pdb.SelectContext(ctx,
		&rows,
		getSignalInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromSignalInfoMaps deletes one or more rows from signal_info_maps table
func (pdb *db) DeleteFromSignalInfoMaps(
	ctx context.Context,
	filter sqlplugin.SignalInfoMapsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInSignalInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.InitiatedIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromSignalInfoMaps deletes all rows from signal_info_maps table
func (pdb *db) DeleteAllFromSignalInfoMaps(
	ctx context.Context,
	filter sqlplugin.SignalInfoMapsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteSignalInfoMapQry,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

// InsertIntoSignalsRequestedSets inserts one or more rows into signals_requested_sets table
func (pdb *db) ReplaceIntoSignalsRequestedSets(
	ctx context.Context,
	rows []sqlplugin.SignalsRequestedSetsRow,
) (sql.Result, error) {
	return pdb.execUpsert(ctx,
		createSignalsRequestedSetQuery,
		rows,
	)
}

// SelectAllFromSignalsRequestedSets reads all rows from signals_requested_sets table
func (pdb *db) SelectAllFromSignalsRequestedSets(
	ctx context.Context,
	filter sqlplugin.SignalsRequestedSetsAllFilter,
) ([]sqlplugin.SignalsRequestedSetsRow, error) {
	var rows []sqlplugin.SignalsRequestedSetsRow
	if err := pdb.SelectContext(ctx,
		&rows,
		getSignalsRequestedSetQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}
	for i := 0; i < len(rows); i++ {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}
	return rows, nil
}

// DeleteFromSignalsRequestedSets deletes one or more rows from signals_requested_sets table
func (pdb *db) DeleteFromSignalsRequestedSets(
	ctx context.Context,
	filter sqlplugin.SignalsRequestedSetsFilter,
) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteSignalsRequestedSetQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		filter.SignalIDs,
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

// DeleteAllFromSignalsRequestedSets deletes all rows from signals_requested_sets table
func (pdb *db) DeleteAllFromSignalsRequestedSets(
	ctx context.Context,
	filter sqlplugin.SignalsRequestedSetsAllFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteAllSignalsRequestedSetQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

var (
	chasmNodeColumns = []string{
		"metadata",
		"metadata_encoding",
		"data",
		"data_encoding",
	}
	chasmNodeTableName = "chasm_node_maps"
	chasmNodeKey       = "chasm_path"

	deleteChasmNodeMapSQLQuery = makeDeleteMapQry(chasmNodeTableName)
	// chasm_node_maps is keyed on chasm_path_hash (a persisted SHA-256 of chasm_path), because
	// chasm_path is too long for an index key. The upsert matches on the hash and the path.
	setKeyInChasmNodeMapSQLQuery = strings.Replace(
		makeSetKeyInMapQry(chasmNodeTableName, chasmNodeColumns, chasmNodeKey),
		"AND t.chasm_path = s.chasm_path",
		"AND t.chasm_path_hash = CAST(HASHBYTES('SHA2_256', s.chasm_path) AS BINARY(32)) AND t.chasm_path = s.chasm_path",
		1,
	)
	deleteKeyInChasmNodeMapSQLQuery = makeDeleteKeyInMapQry(chasmNodeTableName, chasmNodeKey)
	getChasmNodeMapSQLQuery         = makeGetMapQryTemplate(chasmNodeTableName, chasmNodeColumns, chasmNodeKey)
)

func (pdb *db) SelectAllFromChasmNodeMaps(
	ctx context.Context,
	filter sqlplugin.ChasmNodeMapsAllFilter,
) ([]sqlplugin.ChasmNodeMapsRow, error) {
	var rows []sqlplugin.ChasmNodeMapsRow

	if err := pdb.SelectContext(ctx,
		&rows,
		getChasmNodeMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	); err != nil {
		return nil, err
	}

	for i := range rows {
		rows[i].ShardID = filter.ShardID
		rows[i].NamespaceID = filter.NamespaceID
		rows[i].WorkflowID = filter.WorkflowID
		rows[i].RunID = filter.RunID
	}

	return rows, nil
}

func (pdb *db) ReplaceIntoChasmNodeMaps(
	ctx context.Context,
	rows []sqlplugin.ChasmNodeMapsRow,
) (sql.Result, error) {
	// chasm_path is VARBINARY: bind it as bytes. A Go string goes to SQL Server as NVARCHAR, which
	// does not convert to VARBINARY implicitly and would not keep the bytes.
	binRows := make([]chasmNodeMapsRowBinary, len(rows))
	for i, r := range rows {
		binRows[i] = chasmNodeMapsRowBinary{
			ShardID: r.ShardID, NamespaceID: r.NamespaceID, WorkflowID: r.WorkflowID, RunID: r.RunID,
			ChasmPath: []byte(r.ChasmPath), Metadata: r.Metadata, MetadataEncoding: r.MetadataEncoding,
			Data: r.Data, DataEncoding: r.DataEncoding,
		}
	}
	return pdb.execUpsert(ctx,
		setKeyInChasmNodeMapSQLQuery,
		binRows,
	)
}

// chasmNodeMapsRowBinary is sqlplugin.ChasmNodeMapsRow with the path as bytes.
type chasmNodeMapsRowBinary struct {
	ShardID          int32
	NamespaceID      []byte
	WorkflowID       string
	RunID            []byte
	ChasmPath        []byte
	Metadata         []byte
	MetadataEncoding string
	Data             []byte
	DataEncoding     string
}

func (pdb *db) DeleteFromChasmNodeMaps(ctx context.Context, filter sqlplugin.ChasmNodeMapsFilter) (sql.Result, error) {
	query, args, err := sqlx.In(
		deleteKeyInChasmNodeMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
		chasmPathsBinary(filter.ChasmPaths),
	)
	if err != nil {
		return nil, err
	}
	return pdb.ExecContext(ctx,
		pdb.Rebind(query),
		args...,
	)
}

func (pdb *db) DeleteAllFromChasmNodeMaps(ctx context.Context, filter sqlplugin.ChasmNodeMapsAllFilter) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteChasmNodeMapSQLQuery,
		filter.ShardID,
		filter.NamespaceID,
		filter.WorkflowID,
		filter.RunID,
	)
}

func chasmPathsBinary(paths []string) [][]byte {
	out := make([][]byte, len(paths))
	for i, p := range paths {
		out[i] = []byte(p)
	}
	return out
}
