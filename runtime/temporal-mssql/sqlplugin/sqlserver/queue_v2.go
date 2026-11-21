package sqlserver

import (
	"context"
	"database/sql"

	"go.temporal.io/server/common/persistence/sql/sqlplugin"
)

const (
	templateEnqueueMessageQueryV2      = `INSERT INTO queue_messages (queue_type, queue_name, queue_partition, message_id, message_payload, message_encoding) VALUES(:queue_type, :queue_name, :queue_partition, :message_id, :message_payload, :message_encoding)`
	templateGetMessagesQueryV2         = `SELECT message_id, message_payload, message_encoding FROM queue_messages WHERE queue_type = @p1 and queue_name = @p2 and queue_partition = @p3 and message_id >= @p4 ORDER BY message_id ASC OFFSET 0 ROWS FETCH NEXT @p5 ROWS ONLY`
	templateRangeDeleteMessagesQueryV2 = `DELETE FROM queue_messages WHERE queue_type = @p1 and queue_name = @p2 and queue_partition = @p3 and message_id >= @p4 and message_id <= @p5`
	// An update lock on the last row only (see templateGetLastMessageIDQuery in queue.go).
	templateGetLastMessageIDQueryV2          = `SELECT TOP 1 message_id FROM queue_messages WITH (UPDLOCK, ROWLOCK) WHERE queue_type=@p1 and queue_name=@p2 and queue_partition=@p3 ORDER BY message_id DESC`
	templateCreateQueueMetadataQueryV2       = `INSERT INTO queues (queue_type, queue_name, metadata_payload, metadata_encoding) VALUES(:queue_type, :queue_name, :metadata_payload, :metadata_encoding)`
	templateUpdateQueueMetadataQueryV2       = `UPDATE queues SET metadata_payload = :metadata_payload, metadata_encoding = :metadata_encoding WHERE queue_type = :queue_type and queue_name = :queue_name`
	templateGetQueueMetadataQueryV2          = `SELECT metadata_payload, metadata_encoding from queues WHERE queue_type=@p1 and queue_name=@p2`
	templateGetQueueMetadataQueryV2ForUpdate = `SELECT metadata_payload, metadata_encoding from queues WITH (UPDLOCK, ROWLOCK) WHERE queue_type=@p1 and queue_name=@p2`
	templateGetNameFromQueueMetadataV2       = `SELECT queue_type, queue_name, metadata_payload, metadata_encoding from queues WHERE queue_type=@p1 ORDER BY queue_name OFFSET @p3 ROWS FETCH NEXT @p2 ROWS ONLY`
)

func (pdb *db) InsertIntoQueueV2Metadata(ctx context.Context, row *sqlplugin.QueueV2MetadataRow) (sql.Result, error) {
	return pdb.NamedExecContext(ctx,
		templateCreateQueueMetadataQueryV2,
		row,
	)
}

func (pdb *db) UpdateQueueV2Metadata(ctx context.Context, row *sqlplugin.QueueV2MetadataRow) (sql.Result, error) {
	return pdb.NamedExecContext(ctx,
		templateUpdateQueueMetadataQueryV2,
		row,
	)
}

func (pdb *db) SelectFromQueueV2Metadata(ctx context.Context, filter sqlplugin.QueueV2MetadataFilter) (*sqlplugin.QueueV2MetadataRow, error) {
	var row sqlplugin.QueueV2MetadataRow
	err := pdb.GetContext(ctx,
		&row,
		templateGetQueueMetadataQueryV2,
		filter.QueueType,
		filter.QueueName,
	)
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (pdb *db) SelectFromQueueV2MetadataForUpdate(ctx context.Context, filter sqlplugin.QueueV2MetadataFilter) (*sqlplugin.QueueV2MetadataRow, error) {
	var row sqlplugin.QueueV2MetadataRow
	err := pdb.GetContext(ctx,
		&row,
		templateGetQueueMetadataQueryV2ForUpdate,
		filter.QueueType,
		filter.QueueName,
	)
	if err != nil {
		return nil, err
	}
	return &row, nil
}

func (pdb *db) SelectNameFromQueueV2Metadata(ctx context.Context, filter sqlplugin.QueueV2MetadataTypeFilter) ([]sqlplugin.QueueV2MetadataRow, error) {
	var rows []sqlplugin.QueueV2MetadataRow
	err := pdb.SelectContext(ctx,
		&rows,
		templateGetNameFromQueueMetadataV2,
		filter.QueueType,
		filter.PageSize,
		filter.PageOffset,
	)
	if err != nil {
		return nil, err
	}
	return rows, nil
}

func (pdb *db) InsertIntoQueueV2Messages(ctx context.Context, row []sqlplugin.QueueV2MessageRow) (sql.Result, error) {
	return pdb.NamedExecContext(ctx,
		templateEnqueueMessageQueryV2,
		row,
	)
}

func (pdb *db) RangeSelectFromQueueV2Messages(ctx context.Context, filter sqlplugin.QueueV2MessagesFilter) ([]sqlplugin.QueueV2MessageRow, error) {
	var rows []sqlplugin.QueueV2MessageRow
	err := pdb.SelectContext(ctx,
		&rows,
		templateGetMessagesQueryV2,
		filter.QueueType,
		filter.QueueName,
		filter.Partition,
		filter.MinMessageID,
		filter.PageSize,
	)
	return rows, err
}

func (pdb *db) RangeDeleteFromQueueV2Messages(ctx context.Context, filter sqlplugin.QueueV2MessagesFilter) (sql.Result, error) {
	return pdb.ExecContext(ctx,
		templateRangeDeleteMessagesQueryV2,
		filter.QueueType,
		filter.QueueName,
		filter.Partition,
		filter.MinMessageID,
		filter.MaxMessageID,
	)
}

func (pdb *db) GetLastEnqueuedMessageIDForUpdateV2(ctx context.Context, filter sqlplugin.QueueV2Filter) (int64, error) {
	var lastMessageID int64
	err := pdb.GetContext(ctx,
		&lastMessageID,
		templateGetLastMessageIDQueryV2,
		filter.QueueType,
		filter.QueueName,
		filter.Partition,
	)
	return lastMessageID, err
}
