package sqlserver

import (
	"context"
	"database/sql"
	"errors"

	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const (
	createEndpointsTableVersionQry    = `INSERT INTO nexus_endpoints_partition_status(version) VALUES(1)`
	incrementEndpointsTableVersionQry = `UPDATE nexus_endpoints_partition_status SET version = @p1 WHERE version = @p2`
	getEndpointsTableVersionQry       = `SELECT version FROM nexus_endpoints_partition_status`

	createEndpointQry  = `INSERT INTO nexus_endpoints(id, data, data_encoding, version) VALUES (@p1, @p2, @p3, 1)`
	updateEndpointQry  = `UPDATE nexus_endpoints SET data = @p1, data_encoding = @p2, version = @p3 WHERE id = @p4 AND version = @p5`
	deleteEndpointQry  = `DELETE FROM nexus_endpoints WHERE id = @p1`
	getEndpointByIdQry = `SELECT id, data, data_encoding, version FROM nexus_endpoints WHERE id = @p1`
	getEndpointsQry    = `SELECT id, data, data_encoding, version FROM nexus_endpoints WHERE id > @p1 ORDER BY id OFFSET 0 ROWS FETCH NEXT @p2 ROWS ONLY`
)

func (pdb *db) InitializeNexusEndpointsTableVersion(ctx context.Context) (sql.Result, error) {
	return pdb.ExecContext(ctx, createEndpointsTableVersionQry)
}

func (pdb *db) IncrementNexusEndpointsTableVersion(
	ctx context.Context,
	lastKnownTableVersion int64,
) (sql.Result, error) {
	return pdb.ExecContext(ctx, incrementEndpointsTableVersionQry, lastKnownTableVersion+1, lastKnownTableVersion)
}

func (pdb *db) GetNexusEndpointsTableVersion(
	ctx context.Context,
) (int64, error) {
	var version int64
	err := pdb.GetContext(ctx, &version, getEndpointsTableVersionQry)
	if errors.Is(err, sql.ErrNoRows) {
		return 0, nil
	}
	return version, err
}

func (pdb *db) InsertIntoNexusEndpoints(
	ctx context.Context,
	row *sqlplugin.NexusEndpointsRow,
) (sql.Result, error) {
	return pdb.ExecContext(
		ctx,
		createEndpointQry,
		row.ID,
		row.Data,
		row.DataEncoding)
}

func (pdb *db) UpdateNexusEndpoint(
	ctx context.Context,
	row *sqlplugin.NexusEndpointsRow,
) (sql.Result, error) {
	return pdb.ExecContext(
		ctx,
		updateEndpointQry,
		row.Data,
		row.DataEncoding,
		row.Version+1,
		row.ID,
		row.Version)
}

func (pdb *db) DeleteFromNexusEndpoints(
	ctx context.Context,
	id []byte,
) (sql.Result, error) {
	return pdb.ExecContext(ctx, deleteEndpointQry, id)
}

func (pdb *db) GetNexusEndpointByID(
	ctx context.Context,
	id []byte,
) (*sqlplugin.NexusEndpointsRow, error) {
	var row sqlplugin.NexusEndpointsRow
	err := pdb.GetContext(ctx, &row, getEndpointByIdQry, id)
	return &row, err
}

func (pdb *db) ListNexusEndpoints(
	ctx context.Context,
	request *sqlplugin.ListNexusEndpointsRequest,
) ([]sqlplugin.NexusEndpointsRow, error) {
	var rows []sqlplugin.NexusEndpointsRow
	err := pdb.SelectContext(ctx, &rows, getEndpointsQry, request.LastID, request.Limit)
	return rows, err
}
