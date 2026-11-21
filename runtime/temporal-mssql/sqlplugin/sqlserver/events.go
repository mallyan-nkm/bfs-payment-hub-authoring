package sqlserver

import (
	"context"
	"database/sql"

	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const (
	// below are templates for history_node table
	addHistoryNodesQuery = `MERGE INTO history_node WITH (HOLDLOCK) AS t ` +
		`USING (SELECT :shard_id AS shard_id, :tree_id AS tree_id, :branch_id AS branch_id, :node_id AS node_id, :prev_txn_id AS prev_txn_id, :txn_id AS txn_id, :data AS data, :data_encoding AS data_encoding) AS s ` +
		`ON t.shard_id = s.shard_id AND t.tree_id = s.tree_id AND t.branch_id = s.branch_id AND t.node_id = s.node_id AND t.txn_id = s.txn_id ` +
		`WHEN MATCHED THEN UPDATE SET prev_txn_id = s.prev_txn_id, data = s.data, data_encoding = s.data_encoding ` +
		`WHEN NOT MATCHED THEN INSERT (shard_id, tree_id, branch_id, node_id, prev_txn_id, txn_id, data, data_encoding) ` +
		`VALUES (s.shard_id, s.tree_id, s.branch_id, s.node_id, s.prev_txn_id, s.txn_id, s.data, s.data_encoding);`

	getHistoryNodesQuery = `SELECT node_id, prev_txn_id, txn_id, data, data_encoding FROM history_node ` +
		`WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 AND ((node_id = @p4 AND txn_id > @p5) OR node_id > @p6) AND node_id < @p7 ` +
		`ORDER BY shard_id, tree_id, branch_id, node_id, txn_id OFFSET 0 ROWS FETCH NEXT @p8 ROWS ONLY `

	getHistoryNodesReverseQuery = `SELECT node_id, prev_txn_id, txn_id, data, data_encoding FROM history_node ` +
		`WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 AND node_id >= @p4 AND ((node_id = @p5 AND txn_id < @p6) OR node_id < @p7) ` +
		`ORDER BY shard_id, tree_id, branch_id DESC, node_id DESC, txn_id DESC OFFSET 0 ROWS FETCH NEXT @p8 ROWS ONLY `

	getHistoryNodeMetadataQuery = `SELECT node_id, prev_txn_id, txn_id FROM history_node ` +
		`WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 AND ((node_id = @p4 AND txn_id > @p5) OR node_id > @p6) AND node_id < @p7 ` +
		`ORDER BY shard_id, tree_id, branch_id, node_id, txn_id OFFSET 0 ROWS FETCH NEXT @p8 ROWS ONLY `

	deleteHistoryNodeQuery = `DELETE FROM history_node WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 AND node_id = @p4 AND txn_id = @p5 `

	deleteHistoryNodesQuery = `DELETE FROM history_node WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 AND node_id >= @p4 `

	// below are templates for history_tree table
	addHistoryTreeQuery = `MERGE INTO history_tree WITH (HOLDLOCK) AS t ` +
		`USING (SELECT :shard_id AS shard_id, :tree_id AS tree_id, :branch_id AS branch_id, :data AS data, :data_encoding AS data_encoding) AS s ` +
		`ON t.shard_id = s.shard_id AND t.tree_id = s.tree_id AND t.branch_id = s.branch_id ` +
		`WHEN MATCHED THEN UPDATE SET data = s.data, data_encoding = s.data_encoding ` +
		`WHEN NOT MATCHED THEN INSERT (shard_id, tree_id, branch_id, data, data_encoding) ` +
		`VALUES (s.shard_id, s.tree_id, s.branch_id, s.data, s.data_encoding);`

	getHistoryTreeQuery = `SELECT branch_id, data, data_encoding FROM history_tree WHERE shard_id = @p1 AND tree_id = @p2 `

	paginateBranchesQuery = `SELECT shard_id, tree_id, branch_id, data, data_encoding
        FROM history_tree
        WHERE shard_id > @p1 OR (shard_id = @p1 AND (tree_id > @p2 OR (tree_id = @p2 AND branch_id > @p3)))
        ORDER BY shard_id, tree_id, branch_id OFFSET 0 ROWS FETCH NEXT @p4 ROWS ONLY`

	deleteHistoryTreeQuery = `DELETE FROM history_tree WHERE shard_id = @p1 AND tree_id = @p2 AND branch_id = @p3 `
)

// For history_node table:

// InsertIntoHistoryNode inserts a row into history_node table
func (pdb *db) InsertIntoHistoryNode(
	ctx context.Context,
	row *sqlplugin.HistoryNodeRow,
) (sql.Result, error) {
	// NOTE: txn_id is *= -1 within DB
	row.TxnID = -row.TxnID
	return pdb.NamedExecContext(ctx,
		addHistoryNodesQuery,
		row,
	)
}

// DeleteFromHistoryNode delete a row from history_node table
func (pdb *db) DeleteFromHistoryNode(
	ctx context.Context,
	row *sqlplugin.HistoryNodeRow,
) (sql.Result, error) {
	// NOTE: txn_id is *= -1 within DB
	row.TxnID = -row.TxnID
	return pdb.ExecContext(ctx,
		deleteHistoryNodeQuery,
		row.ShardID,
		row.TreeID,
		row.BranchID,
		row.NodeID,
		row.TxnID,
	)
}

// SelectFromHistoryNode reads one or more rows from history_node table
func (pdb *db) RangeSelectFromHistoryNode(
	ctx context.Context,
	filter sqlplugin.HistoryNodeSelectFilter,
) ([]sqlplugin.HistoryNodeRow, error) {
	var query string
	if filter.MetadataOnly {
		query = getHistoryNodeMetadataQuery
	} else if filter.ReverseOrder {
		query = getHistoryNodesReverseQuery
	} else {
		query = getHistoryNodesQuery
	}

	var args []any
	if filter.ReverseOrder {
		args = []any{
			filter.ShardID,
			filter.TreeID,
			filter.BranchID,
			filter.MinNodeID,
			filter.MaxTxnID,
			-filter.MaxTxnID,
			filter.MaxNodeID,
			filter.PageSize,
		}
	} else {
		args = []any{
			filter.ShardID,
			filter.TreeID,
			filter.BranchID,
			filter.MinNodeID,
			-filter.MinTxnID, // NOTE: transaction ID is *= -1 when stored
			filter.MinNodeID,
			filter.MaxNodeID,
			filter.PageSize,
		}
	}

	var rows []sqlplugin.HistoryNodeRow
	err := pdb.SelectContext(ctx, &rows, query, args...)
	if err != nil {
		return nil, err
	}
	// NOTE: since we let txn_id multiple by -1 when inserting, we have to revert it back here
	for index := range rows {
		rows[index].TxnID = -rows[index].TxnID
	}
	return rows, nil
}

// DeleteFromHistoryNode deletes one or more rows from history_node table
func (pdb *db) RangeDeleteFromHistoryNode(
	ctx context.Context,
	filter sqlplugin.HistoryNodeDeleteFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteHistoryNodesQuery,
		filter.ShardID,
		filter.TreeID,
		filter.BranchID,
		filter.MinNodeID,
	)
}

// For history_tree table:

// InsertIntoHistoryTree inserts a row into history_tree table
func (pdb *db) InsertIntoHistoryTree(
	ctx context.Context,
	row *sqlplugin.HistoryTreeRow,
) (sql.Result, error) {
	return pdb.NamedExecContext(ctx,
		addHistoryTreeQuery,
		row,
	)
}

// SelectFromHistoryTree reads one or more rows from history_tree table
func (pdb *db) SelectFromHistoryTree(
	ctx context.Context,
	filter sqlplugin.HistoryTreeSelectFilter,
) ([]sqlplugin.HistoryTreeRow, error) {
	var rows []sqlplugin.HistoryTreeRow
	err := pdb.SelectContext(ctx,
		&rows,
		getHistoryTreeQuery,
		filter.ShardID,
		filter.TreeID,
	)
	return rows, err
}

// PaginateBranchesFromHistoryTree reads up to page.Limit rows from the history_tree table sorted by their primary key,
// while skipping the first page.Offset rows.
func (pdb *db) PaginateBranchesFromHistoryTree(
	ctx context.Context,
	page sqlplugin.HistoryTreeBranchPage,
) ([]sqlplugin.HistoryTreeRow, error) {
	var rows []sqlplugin.HistoryTreeRow
	err := pdb.SelectContext(ctx,
		&rows,
		paginateBranchesQuery,
		page.ShardID,
		page.TreeID,
		page.BranchID,
		page.Limit,
	)
	return rows, err
}

// DeleteFromHistoryTree deletes one or more rows from history_tree table
func (pdb *db) DeleteFromHistoryTree(
	ctx context.Context,
	filter sqlplugin.HistoryTreeDeleteFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		deleteHistoryTreeQuery,
		filter.ShardID,
		filter.TreeID,
		filter.BranchID,
	)
}
