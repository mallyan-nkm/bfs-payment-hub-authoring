package sqlserver

import (
	"context"
	"database/sql"
	"strconv"
	"strings"

	p "go.temporal.io/server/common/persistence"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const constMetadataPartition = 0
const constMembershipPartition = 0
const (
	// ****** CLUSTER_METADATA_INFO TABLE ******
	insertClusterMetadataQry = `INSERT INTO cluster_metadata_info (metadata_partition, cluster_name, data, data_encoding, version) VALUES(@p1, @p2, @p3, @p4, @p5)`

	updateClusterMetadataQry = `UPDATE cluster_metadata_info SET data = @p1, data_encoding = @p2, version = @p3 WHERE metadata_partition = @p4 AND cluster_name = @p5`

	getClusterMetadataBase      = `SELECT data, data_encoding, version FROM cluster_metadata_info `
	getClusterMetadataQry       = getClusterMetadataBase + `WHERE metadata_partition = @p1 AND cluster_name = @p2`
	listClusterMetadataQry      = getClusterMetadataBase + `WHERE metadata_partition = @p1 ORDER BY cluster_name OFFSET 0 ROWS FETCH NEXT @p2 ROWS ONLY`
	listClusterMetadataRangeQry = getClusterMetadataBase + `WHERE metadata_partition = @p1 AND cluster_name > @p2 ORDER BY cluster_name OFFSET 0 ROWS FETCH NEXT @p3 ROWS ONLY`

	writeLockGetClusterMetadataQry = `SELECT data, data_encoding, version FROM cluster_metadata_info WITH (UPDLOCK, ROWLOCK) WHERE metadata_partition = @p1 AND cluster_name = @p2`

	deleteClusterMetadataQry = `DELETE FROM cluster_metadata_info WHERE metadata_partition = @p1 AND cluster_name = @p2`

	// ****** CLUSTER_MEMBERSHIP TABLE ******
	templateUpsertActiveClusterMembership = `MERGE INTO cluster_membership WITH (HOLDLOCK) AS t
USING (SELECT @p1 AS membership_partition, @p2 AS host_id) AS s
ON t.membership_partition = s.membership_partition AND t.host_id = s.host_id
WHEN MATCHED THEN UPDATE SET rpc_address = @p3, rpc_port = @p4, role = @p5, session_start = @p6, last_heartbeat = @p7, record_expiry = @p8
WHEN NOT MATCHED THEN INSERT (membership_partition, host_id, rpc_address, rpc_port, role, session_start, last_heartbeat, record_expiry)
VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8);`

	templatePruneStaleClusterMembership = `DELETE FROM
cluster_membership
WHERE host_id IN (
SELECT host_id FROM cluster_membership WHERE membership_partition = @p1 AND record_expiry < @p2)`

	templateGetClusterMembership = `SELECT host_id, rpc_address, rpc_port, role, session_start, last_heartbeat, record_expiry FROM
cluster_membership WHERE membership_partition = @p`

	// ClusterMembership WHERE Suffixes
	templateWithRoleSuffix           = ` AND role = @p`
	templateWithHeartbeatSinceSuffix = ` AND last_heartbeat > @p`
	templateWithRecordExpirySuffix   = ` AND record_expiry > @p`
	templateWithRPCAddressSuffix     = ` AND rpc_address = @p`
	templateWithHostIDSuffix         = ` AND host_id = @p`
	templateWithHostIDGreaterSuffix  = ` AND host_id > @p`
	templateWithSessionStartSuffix   = ` AND session_start >= @p`

	// Generic SELECT Suffixes
	templateWithLimitSuffix               = ` OFFSET 0 ROWS FETCH NEXT @p`
	templateWithLimitSuffixEnd            = ` ROWS ONLY`
	templateWithOrderBySessionStartSuffix = ` ORDER BY membership_partition ASC, host_id ASC`
)

func (pdb *db) SaveClusterMetadata(
	ctx context.Context,
	row *sqlplugin.ClusterMetadataRow,
) (sql.Result, error) {
	if row.Version == 0 {
		return pdb.ExecContext(ctx,
			insertClusterMetadataQry,
			constMetadataPartition,
			row.ClusterName,
			row.Data,
			row.DataEncoding,
			1,
		)
	}
	return pdb.ExecContext(ctx,
		updateClusterMetadataQry,
		row.Data,
		row.DataEncoding,
		row.Version+1,
		constMetadataPartition,
		row.ClusterName,
	)
}

func (pdb *db) ListClusterMetadata(
	ctx context.Context,
	filter *sqlplugin.ClusterMetadataFilter,
) ([]sqlplugin.ClusterMetadataRow, error) {
	var err error
	var rows []sqlplugin.ClusterMetadataRow
	switch {
	case len(filter.ClusterName) != 0:
		err = pdb.SelectContext(ctx,
			&rows,
			listClusterMetadataRangeQry,
			constMetadataPartition,
			filter.ClusterName,
			filter.PageSize,
		)
	default:
		err = pdb.SelectContext(ctx,
			&rows,
			listClusterMetadataQry,
			constMetadataPartition,
			filter.PageSize,
		)
	}
	return rows, err
}

func (pdb *db) GetClusterMetadata(
	ctx context.Context,
	filter *sqlplugin.ClusterMetadataFilter,
) (*sqlplugin.ClusterMetadataRow, error) {
	var row sqlplugin.ClusterMetadataRow
	err := pdb.GetContext(ctx,
		&row,
		getClusterMetadataQry,
		constMetadataPartition,
		filter.ClusterName,
	)
	if err != nil {
		return nil, err
	}
	return &row, err
}

func (pdb *db) WriteLockGetClusterMetadata(
	ctx context.Context,
	filter *sqlplugin.ClusterMetadataFilter,
) (*sqlplugin.ClusterMetadataRow, error) {
	var row sqlplugin.ClusterMetadataRow
	err := pdb.GetContext(ctx,
		&row,
		writeLockGetClusterMetadataQry,
		constMetadataPartition,
		filter.ClusterName,
	)
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (pdb *db) DeleteClusterMetadata(
	ctx context.Context,
	filter *sqlplugin.ClusterMetadataFilter,
) (sql.Result, error) {

	return pdb.ExecContext(ctx,
		deleteClusterMetadataQry,
		constMetadataPartition,
		filter.ClusterName,
	)
}

func (pdb *db) UpsertClusterMembership(
	ctx context.Context,
	row *sqlplugin.ClusterMembershipRow,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		templateUpsertActiveClusterMembership,
		constMembershipPartition,
		row.HostID,
		row.RPCAddress,
		row.RPCPort,
		row.Role,
		row.SessionStart,
		row.LastHeartbeat,
		row.RecordExpiry)
}

func (pdb *db) GetClusterMembers(
	ctx context.Context,
	filter *sqlplugin.ClusterMembershipFilter,
) ([]sqlplugin.ClusterMembershipRow, error) {
	var queryString strings.Builder
	var operands []any
	queryString.WriteString(templateGetClusterMembership)
	operands = append(operands, constMembershipPartition)
	queryString.WriteString(strconv.Itoa(len(operands)))

	if filter.HostIDEquals != nil {
		queryString.WriteString(templateWithHostIDSuffix)
		operands = append(operands, filter.HostIDEquals)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if filter.RPCAddressEquals != "" {
		queryString.WriteString(templateWithRPCAddressSuffix)
		operands = append(operands, filter.RPCAddressEquals)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if filter.RoleEquals != p.All {
		queryString.WriteString(templateWithRoleSuffix)
		operands = append(operands, filter.RoleEquals)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if !filter.LastHeartbeatAfter.IsZero() {
		queryString.WriteString(templateWithHeartbeatSinceSuffix)
		operands = append(operands, filter.LastHeartbeatAfter)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if !filter.RecordExpiryAfter.IsZero() {
		queryString.WriteString(templateWithRecordExpirySuffix)
		operands = append(operands, filter.RecordExpiryAfter)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if !filter.SessionStartedAfter.IsZero() {
		queryString.WriteString(templateWithSessionStartSuffix)
		operands = append(operands, filter.SessionStartedAfter)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	if filter.HostIDGreaterThan != nil {
		queryString.WriteString(templateWithHostIDGreaterSuffix)
		operands = append(operands, filter.HostIDGreaterThan)
		queryString.WriteString(strconv.Itoa(len(operands)))
	}

	queryString.WriteString(templateWithOrderBySessionStartSuffix)

	if filter.MaxRecordCount > 0 {
		queryString.WriteString(templateWithLimitSuffix)
		operands = append(operands, filter.MaxRecordCount)
		queryString.WriteString(strconv.Itoa(len(operands)))
		queryString.WriteString(templateWithLimitSuffixEnd)
	}

	compiledQryString := queryString.String()

	var rows []sqlplugin.ClusterMembershipRow
	err := pdb.SelectContext(ctx, &rows,
		compiledQryString,
		operands...)
	if err != nil {
		return nil, err
	}
	for i := range rows {
		rows[i].SessionStart = pdb.converter.FromSQLServerDateTime(rows[i].SessionStart)
		rows[i].LastHeartbeat = pdb.converter.FromSQLServerDateTime(rows[i].LastHeartbeat)
		rows[i].RecordExpiry = pdb.converter.FromSQLServerDateTime(rows[i].RecordExpiry)
	}
	return rows, nil
}

func (pdb *db) PruneClusterMembership(
	ctx context.Context,
	filter *sqlplugin.PruneClusterMembershipFilter,
) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		templatePruneStaleClusterMembership,
		constMembershipPartition,
		filter.PruneRecordsBefore,
	)
}
