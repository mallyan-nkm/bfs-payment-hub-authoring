-- Temporal persistence schema for SQL Server 2022, version 1.19.
-- A port of schema/mysql/v8/temporal/schema.sql and schema/postgresql/v12/temporal/schema.sql at
-- go.temporal.io/server v1.32.2. The tables, columns and keys are the same; only the types differ.
--
-- The database MUST be created with the collation Latin1_General_100_BIN2_UTF8 (the plugin's
-- CreateDatabase does this). VARCHAR then holds UTF-8, one byte per ASCII character, and compares
-- byte by byte, as Temporal expects. NVARCHAR would double every key column and push four keys over
-- the 900-byte limit of a clustered key.
--
-- Statements are split on semicolons by Temporal's schema tool, so no statement here may contain
-- a CASE expression or an IF keyword.

CREATE TABLE namespaces(
  partition_id INT NOT NULL,
  id BINARY(16) NOT NULL,
  name VARCHAR(255) NOT NULL CONSTRAINT namespaces_name_unique UNIQUE,
  notification_version BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  is_global BIT NOT NULL,
  PRIMARY KEY(partition_id, id)
);

CREATE TABLE namespace_metadata (
  partition_id INT NOT NULL,
  notification_version BIGINT NOT NULL,
  PRIMARY KEY(partition_id)
);

INSERT INTO namespace_metadata (partition_id, notification_version) VALUES (54321, 1);

CREATE TABLE shards (
  shard_id INT NOT NULL,
  range_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id)
);

CREATE TABLE executions(
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  next_event_id BIGINT NOT NULL,
  last_write_version BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  state VARBINARY(MAX) NOT NULL,
  state_encoding VARCHAR(16) NOT NULL,
  db_record_version BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id)
);

CREATE TABLE current_executions(
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  create_request_id VARCHAR(255) NOT NULL,
  state INT NOT NULL,
  status INT NOT NULL,
  start_version BIGINT NOT NULL DEFAULT 0,
  start_time DATETIME2(6),
  last_write_version BIGINT NOT NULL,
  data VARBINARY(MAX) NULL,
  data_encoding VARCHAR(16) NOT NULL DEFAULT '',
  PRIMARY KEY (shard_id, namespace_id, workflow_id)
);

CREATE TABLE current_chasm_executions(
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  business_id VARCHAR(255) NOT NULL,
  archetype_id BIGINT NOT NULL,
  run_id BINARY(16) NOT NULL,
  create_request_id VARCHAR(255) NOT NULL,
  state INT NOT NULL,
  status INT NOT NULL,
  start_version BIGINT NOT NULL DEFAULT 0,
  start_time DATETIME2(6),
  last_write_version BIGINT NOT NULL,
  data VARBINARY(MAX) NULL,
  data_encoding VARCHAR(16) NOT NULL DEFAULT '',
  PRIMARY KEY (shard_id, namespace_id, business_id, archetype_id)
);

CREATE TABLE buffered_events (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT buffered_events_id_unique UNIQUE,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, id)
);

CREATE TABLE tasks (
  range_hash BIGINT NOT NULL,
  task_queue_id VARBINARY(272) NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (range_hash, task_queue_id, task_id)
);

CREATE TABLE task_queues (
  range_hash BIGINT NOT NULL,
  task_queue_id VARBINARY(272) NOT NULL,
  range_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (range_hash, task_queue_id)
);

CREATE TABLE tasks_v2 (
  range_hash BIGINT NOT NULL,
  task_queue_id VARBINARY(272) NOT NULL,
  pass BIGINT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (range_hash, task_queue_id, pass, task_id)
);

CREATE TABLE task_queues_v2 (
  range_hash BIGINT NOT NULL,
  task_queue_id VARBINARY(272) NOT NULL,
  range_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (range_hash, task_queue_id)
);

CREATE TABLE task_queue_user_data (
  namespace_id    BINARY(16) NOT NULL,
  task_queue_name VARCHAR(255) NOT NULL,
  data            VARBINARY(MAX) NOT NULL,
  data_encoding   VARCHAR(16) NOT NULL,
  version         BIGINT NOT NULL,
  PRIMARY KEY (namespace_id, task_queue_name)
);

CREATE TABLE build_id_to_task_queue (
  namespace_id    BINARY(16) NOT NULL,
  build_id        VARCHAR(255) NOT NULL,
  task_queue_name VARCHAR(255) NOT NULL,
  PRIMARY KEY (namespace_id, build_id, task_queue_name)
);

CREATE TABLE history_immediate_tasks(
  shard_id INT NOT NULL,
  category_id INT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, category_id, task_id)
);

CREATE TABLE history_scheduled_tasks (
  shard_id INT NOT NULL,
  category_id INT NOT NULL,
  visibility_timestamp DATETIME2(6) NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, category_id, visibility_timestamp, task_id)
);

CREATE TABLE transfer_tasks(
  shard_id INT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, task_id)
);

CREATE TABLE timer_tasks (
  shard_id INT NOT NULL,
  visibility_timestamp DATETIME2(6) NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, visibility_timestamp, task_id)
);

CREATE TABLE replication_tasks (
  shard_id INT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, task_id)
);

CREATE TABLE replication_tasks_dlq (
  source_cluster_name VARCHAR(255) NOT NULL,
  shard_id INT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (source_cluster_name, shard_id, task_id)
);

CREATE TABLE visibility_tasks(
  shard_id INT NOT NULL,
  task_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, task_id)
);

CREATE TABLE activity_info_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  schedule_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, schedule_id)
);

CREATE TABLE timer_info_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  timer_id VARCHAR(255) NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, timer_id)
);

CREATE TABLE child_execution_info_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  initiated_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, initiated_id)
);

CREATE TABLE request_cancel_info_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  initiated_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, initiated_id)
);

CREATE TABLE signal_info_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  initiated_id BIGINT NOT NULL,
  data VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, initiated_id)
);

CREATE TABLE signals_requested_sets (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  signal_id VARCHAR(255) NOT NULL,
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, signal_id)
);

-- chasm_path is up to 1536 bytes, so a key on it is over both index key limits (900 clustered,
-- 1700 nonclustered). The key holds its SHA-256 instead; the plugin compares the hash and the path.
CREATE TABLE chasm_node_maps (
  shard_id INT NOT NULL,
  namespace_id BINARY(16) NOT NULL,
  workflow_id VARCHAR(255) NOT NULL,
  run_id BINARY(16) NOT NULL,
  chasm_path VARBINARY(1536) NOT NULL,
  chasm_path_hash AS CAST(HASHBYTES('SHA2_256', chasm_path) AS BINARY(32)) PERSISTED NOT NULL,
  metadata VARBINARY(MAX) NOT NULL,
  metadata_encoding VARCHAR(16),
  data VARBINARY(MAX),
  data_encoding VARCHAR(16),
  PRIMARY KEY (shard_id, namespace_id, workflow_id, run_id, chasm_path_hash)
);

CREATE TABLE history_node (
  shard_id       INT NOT NULL,
  tree_id        BINARY(16) NOT NULL,
  branch_id      BINARY(16) NOT NULL,
  node_id        BIGINT NOT NULL,
  txn_id         BIGINT NOT NULL,
  prev_txn_id    BIGINT NOT NULL DEFAULT 0,
  data           VARBINARY(MAX) NOT NULL,
  data_encoding  VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, tree_id, branch_id, node_id, txn_id)
);

CREATE TABLE history_tree (
  shard_id       INT NOT NULL,
  tree_id        BINARY(16) NOT NULL,
  branch_id      BINARY(16) NOT NULL,
  data           VARBINARY(MAX) NOT NULL,
  data_encoding  VARCHAR(16) NOT NULL,
  PRIMARY KEY (shard_id, tree_id, branch_id)
);

CREATE TABLE queue (
  queue_type        INT NOT NULL,
  message_id        BIGINT NOT NULL,
  message_payload   VARBINARY(MAX) NOT NULL,
  message_encoding  VARCHAR(16) NOT NULL,
  PRIMARY KEY(queue_type, message_id)
);

CREATE TABLE queue_metadata (
  queue_type     INT NOT NULL,
  data           VARBINARY(MAX) NOT NULL,
  data_encoding  VARCHAR(16) NOT NULL,
  version        BIGINT NOT NULL,
  PRIMARY KEY(queue_type)
);

CREATE TABLE cluster_metadata_info (
  metadata_partition        INT NOT NULL,
  cluster_name              VARCHAR(255) NOT NULL,
  data                      VARBINARY(MAX) NOT NULL,
  data_encoding             VARCHAR(16) NOT NULL,
  version                   BIGINT NOT NULL,
  PRIMARY KEY(metadata_partition, cluster_name)
);

CREATE TABLE cluster_membership (
  membership_partition INT NOT NULL,
  host_id              BINARY(16) NOT NULL,
  rpc_address          VARCHAR(128) NOT NULL,
  rpc_port             INT NOT NULL,
  role                 SMALLINT NOT NULL,
  session_start        DATETIME2(6) DEFAULT '1970-01-02 00:00:01',
  last_heartbeat       DATETIME2(6) DEFAULT '1970-01-02 00:00:01',
  record_expiry        DATETIME2(6) DEFAULT '1970-01-02 00:00:01',
  INDEX cm_idx_rolehost NONCLUSTERED (role, host_id),
  INDEX cm_idx_rolelasthb NONCLUSTERED (role, last_heartbeat),
  INDEX cm_idx_rpchost NONCLUSTERED (rpc_address, role),
  INDEX cm_idx_lasthb NONCLUSTERED (last_heartbeat),
  INDEX cm_idx_recordexpiry NONCLUSTERED (record_expiry),
  PRIMARY KEY (membership_partition, host_id)
);

CREATE TABLE queues (
  queue_type INT NOT NULL,
  queue_name VARCHAR(255) NOT NULL,
  metadata_payload VARBINARY(MAX) NOT NULL,
  metadata_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (queue_type, queue_name)
);

CREATE TABLE queue_messages (
  queue_type INT NOT NULL,
  queue_name VARCHAR(255) NOT NULL,
  queue_partition BIGINT NOT NULL,
  message_id BIGINT NOT NULL,
  message_payload VARBINARY(MAX) NOT NULL,
  message_encoding VARCHAR(16) NOT NULL,
  PRIMARY KEY (queue_type, queue_name, queue_partition, message_id)
);

CREATE TABLE nexus_endpoints (
  id            BINARY(16) NOT NULL,
  data          VARBINARY(MAX) NOT NULL,
  data_encoding VARCHAR(16) NOT NULL,
  version       BIGINT NOT NULL,
  PRIMARY KEY (id)
);

CREATE TABLE nexus_endpoints_partition_status (
  id      INT NOT NULL DEFAULT 0 CHECK (id = 0),
  version BIGINT NOT NULL,
  PRIMARY KEY (id)
);
