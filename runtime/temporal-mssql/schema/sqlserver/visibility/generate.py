#!/usr/bin/env python3
"""Write the SQL Server visibility schema: schema.sql and versioned/v1.14/schema.sql.

usage: python3 schema/sqlserver/visibility/generate.py   (from runtime/temporal-mssql)

The columns follow schema/postgresql/v12/visibility/schema.sql at go.temporal.io/server v1.32.2.
PostgreSQL and MySQL derive one generated column per search attribute from the search_attributes
JSON; on SQL Server each is a computed column over JSON_VALUE, PERSISTED where it is indexed. A
Keyword List attribute (a JSON array) cannot be indexed as a column, so a trigger keeps its values
in the side table executions_visibility_keyword_lists, keyed on a SHA-256 of each value.

Temporal's schema tool splits on semicolons and tracks BEGIN/END, IF and LOOP, so the output has no
CASE and no IF.
"""
import pathlib

HERE = pathlib.Path(__file__).resolve().parent

KEYWORD, BOOL, DATETIME, DOUBLE, INT, TEXT, KEYWORD_LIST = 'keyword', 'bool', 'datetime', 'double', 'int', 'text', 'keyword_list'

# (column, type, index name or None). The order is PostgreSQL's.
PREDEFINED = [
    ('TemporalChangeVersion', KEYWORD_LIST, None),
    ('BinaryChecksums', KEYWORD_LIST, None),
    ('BatcherUser', KEYWORD, 'by_batcher_user'),
    ('TemporalScheduledStartTime', DATETIME, 'by_temporal_scheduled_start_time'),
    ('TemporalScheduledById', KEYWORD, 'by_temporal_scheduled_by_id'),
    ('TemporalSchedulePaused', BOOL, 'by_temporal_schedule_paused'),
    ('TemporalNamespaceDivision', KEYWORD, 'by_temporal_namespace_division'),
    ('BuildIds', KEYWORD_LIST, None),
    ('TemporalPauseInfo', KEYWORD_LIST, None),
    ('TemporalReportedProblems', KEYWORD_LIST, None),
    ('TemporalWorkerDeploymentVersion', KEYWORD, 'by_temporal_worker_deployment_version'),
    ('TemporalWorkflowVersioningBehavior', KEYWORD, 'by_temporal_workflow_versioning_behavior'),
    ('TemporalWorkerDeployment', KEYWORD, 'by_temporal_worker_deployment'),
    ('TemporalUsedWorkerDeploymentVersions', KEYWORD_LIST, None),
    ('TemporalExternalPayloadSizeBytes', INT, 'by_temporal_external_payload_size_bytes'),
    ('TemporalExternalPayloadCount', INT, 'by_temporal_external_payload_count'),
]

CUSTOM = (
    [(f'Bool0{i}', BOOL, f'by_bool_0{i}') for i in range(1, 4)]
    + [(f'Datetime0{i}', DATETIME, f'by_datetime_0{i}') for i in range(1, 4)]
    + [(f'Double0{i}', DOUBLE, f'by_double_0{i}') for i in range(1, 4)]
    + [(f'Int0{i}', INT, f'by_int_0{i}') for i in range(1, 4)]
    + [(f'Keyword{i:02d}', KEYWORD, f'by_keyword_{i:02d}') for i in range(1, 11)]
    + [(f'Text0{i}', TEXT, None) for i in range(1, 4)]
    + [(f'KeywordList0{i}', KEYWORD_LIST, None) for i in range(1, 4)]
)

CHASM = [
    ('TemporalBool01', BOOL, 'by_temporal_bool_01'),
    ('TemporalBool02', BOOL, 'by_temporal_bool_02'),
    ('TemporalDatetime01', DATETIME, 'by_temporal_datetime_01'),
    ('TemporalDatetime02', DATETIME, 'by_temporal_datetime_02'),
    ('TemporalDouble01', DOUBLE, 'by_temporal_double_01'),
    ('TemporalDouble02', DOUBLE, 'by_temporal_double_02'),
    ('TemporalInt01', INT, 'by_temporal_int_01'),
    ('TemporalInt02', INT, 'by_temporal_int_02'),
    ('TemporalKeyword01', KEYWORD, 'by_temporal_keyword_01'),
    ('TemporalKeyword02', KEYWORD, 'by_temporal_keyword_02'),
    ('TemporalKeyword03', KEYWORD, 'by_temporal_keyword_03'),
    ('TemporalKeyword04', KEYWORD, 'by_temporal_keyword_04'),
    ('TemporalLowCardinalityKeyword01', KEYWORD, 'by_temporal_low_cardinality_keyword_01'),
    ('TemporalKeywordList01', KEYWORD_LIST, None),
    ('TemporalKeywordList02', KEYWORD_LIST, None),
]

# The system columns indexed for list filters, as on PostgreSQL.
SYSTEM_INDEXES = [
    ('by_execution_time', 'execution_time'), ('by_workflow_id', 'workflow_id'),
    ('by_workflow_type', 'workflow_type_name'), ('by_status', 'status'),
    ('by_history_length', 'history_length'), ('by_history_size_bytes', 'history_size_bytes'),
    ('by_execution_duration', 'execution_duration'), ('by_state_transition_count', 'state_transition_count'),
    ('by_task_queue', 'task_queue'), ('by_parent_workflow_id', 'parent_workflow_id'),
    ('by_parent_run_id', 'parent_run_id'), ('by_root_workflow_id', 'root_workflow_id'),
    ('by_root_run_id', 'root_run_id'),
]


def computed(name, kind):
    j = f"JSON_VALUE(search_attributes, '$.{name}')"
    return {
        KEYWORD: f'CAST({j} AS VARCHAR(255)) PERSISTED',
        BOOL: f"CAST(IIF({j} = 'true', 1, IIF({j} = 'false', 0, NULL)) AS BIT) PERSISTED",
        # The plugin writes datetimes in UTC as RFC 3339 (...T15:04:05.999999999Z). DATETIME2(6)
        # keeps microseconds, as PostgreSQL's TIMESTAMP does.
        DATETIME: f"TRY_CONVERT(DATETIME2(6), LEFT(REPLACE({j}, 'Z', ''), 26), 126) PERSISTED",
        DOUBLE: f'TRY_CAST({j} AS FLOAT) PERSISTED',
        INT: f'TRY_CAST({j} AS BIGINT) PERSISTED',
        TEXT: f'CAST({j} AS NVARCHAR(4000))',
        KEYWORD_LIST: f"JSON_QUERY(search_attributes, '$.{name}')",
    }[kind]


SORT = 'close_time_or_max DESC, start_time DESC, run_id'


def schema():
    out = []
    w = out.append
    w('-- Generated by schema/sqlserver/visibility/generate.py. Edit that script, not this file.')
    w('-- Temporal visibility schema for SQL Server 2022, version 1.14, after')
    w('-- schema/postgresql/v12/visibility/schema.sql at go.temporal.io/server v1.32.2.')
    w('-- The database MUST be created with the collation Latin1_General_100_BIN2_UTF8.')
    w('')
    w('CREATE TABLE executions_visibility (')
    cols = [
        'namespace_id            CHAR(64)       NOT NULL',
        'run_id                  CHAR(64)       NOT NULL',
        '_version                BIGINT         NOT NULL DEFAULT 0',
        'start_time              DATETIME2(6)   NOT NULL',
        'execution_time          DATETIME2(6)   NOT NULL',
        'workflow_id             VARCHAR(255)   NOT NULL',
        'workflow_type_name      VARCHAR(255)   NOT NULL',
        'status                  INT            NOT NULL',
        'close_time              DATETIME2(6)   NULL',
        'history_length          BIGINT         NULL',
        'history_size_bytes      BIGINT         NULL',
        'execution_duration      BIGINT         NULL',
        'state_transition_count  BIGINT         NULL',
        'memo                    VARBINARY(MAX) NULL',
        'encoding                VARCHAR(64)    NOT NULL',
        "task_queue              VARCHAR(255)   NOT NULL DEFAULT ''",
        'search_attributes       NVARCHAR(MAX)  NULL CONSTRAINT executions_visibility_sa_json CHECK (ISJSON(search_attributes) = 1)',
        'parent_workflow_id      VARCHAR(255)   NULL',
        'parent_run_id           VARCHAR(255)   NULL',
        "root_workflow_id        VARCHAR(255)   NOT NULL DEFAULT ''",
        "root_run_id             VARCHAR(255)   NOT NULL DEFAULT ''",
        # The list order key, COALESCE(close_time, max) on PostgreSQL, as an indexed column.
        "close_time_or_max       AS COALESCE(close_time, CONVERT(DATETIME2(6), '9999-12-31 23:59:59', 120)) PERSISTED",
    ]
    for name, kind, _ in PREDEFINED + CUSTOM + CHASM:
        cols.append(f'{name:<36} AS {computed(name, kind)}')
    cols.append('PRIMARY KEY (namespace_id, run_id)')
    w(',\n'.join('  ' + c for c in cols))
    w(');')
    w('')
    w(f'CREATE INDEX default_idx ON executions_visibility (namespace_id, {SORT});')
    for idx, col in SYSTEM_INDEXES:
        w(f'CREATE INDEX {idx} ON executions_visibility (namespace_id, {col}, {SORT});')
    for name, kind, idx in PREDEFINED + CUSTOM + CHASM:
        if idx:
            w(f'CREATE INDEX {idx} ON executions_visibility (namespace_id, {name}, {SORT});')
    w('')
    w('-- One row per value of each Keyword List attribute of each execution, kept by the trigger below.')
    w('-- The key holds a SHA-256 of the value (as NVARCHAR), so a value of any length is indexed.')
    w('CREATE TABLE executions_visibility_keyword_lists (')
    w('  namespace_id      CHAR(64)     NOT NULL,')
    w('  run_id            CHAR(64)     NOT NULL,')
    w('  search_attribute  VARCHAR(64)  NOT NULL,')
    w('  value_hash        BINARY(32)   NOT NULL,')
    w('  PRIMARY KEY (namespace_id, run_id, search_attribute, value_hash)')
    w(');')
    w('')
    w('CREATE INDEX by_keyword_list_value ON executions_visibility_keyword_lists (namespace_id, search_attribute, value_hash, run_id);')
    w('')
    w('CREATE TRIGGER executions_visibility_keyword_lists_sync ON executions_visibility')
    w('AFTER INSERT, UPDATE, DELETE AS')
    w('BEGIN')
    w('  SET NOCOUNT ON;')
    w('  DELETE kl FROM executions_visibility_keyword_lists kl')
    w('    INNER JOIN deleted d ON kl.namespace_id = d.namespace_id AND kl.run_id = d.run_id;')
    w('  INSERT INTO executions_visibility_keyword_lists (namespace_id, run_id, search_attribute, value_hash)')
    w("    SELECT DISTINCT i.namespace_id, i.run_id, CAST(a.[key] AS VARCHAR(64)), CAST(HASHBYTES('SHA2_256', v.value) AS BINARY(32))")
    w('    FROM inserted i')
    w('    CROSS APPLY OPENJSON(i.search_attributes) a')
    w("    CROSS APPLY OPENJSON(IIF(a.type = 4, a.value, N'[]')) v")
    w('    WHERE i.search_attributes IS NOT NULL AND a.type = 4 AND v.type = 1;')
    w('END;')
    return '\n'.join(out) + '\n'


def main():
    text = schema()
    (HERE / 'schema.sql').write_text(text)
    (HERE / 'versioned' / 'v1.14').mkdir(parents=True, exist_ok=True)
    (HERE / 'versioned' / 'v1.14' / 'schema.sql').write_text(text)
    print('wrote schema.sql and versioned/v1.14/schema.sql')


if __name__ == '__main__':
    main()
