# P4 spike: Temporal on SQL Server

**Built since.** D2 was confirmed and the plugin is built in `runtime/temporal-mssql/`. Its README says what is proved. Two findings below changed on the way: visibility is on SQL Server too, with computed columns and a side table for keyword lists, and row locks for the queues take no key-range lock (no HOLDLOCK), because that deadlocked.

Decision D2 asks for Temporal's persistence on SQL Server. Temporal supports MySQL, PostgreSQL, SQLite and Cassandra, and not SQL Server. This spike measures what a SQL Server plugin would take. Everything below was measured against Temporal server v1.32.2, the latest stable release on the Go module proxy on 2026-11-21, and SQL Server 2022 in Docker.

## How to repeat it

```
curl -sSfL -o t.zip https://proxy.golang.org/go.temporal.io/server/@v/v1.32.2.zip && unzip -q t.zip -d src
python3 runtime/tools/temporal-sqlserver/translate.py src/go.temporal.io/server@v1.32.2 out
# SQL Server 2022: CREATE DATABASE temporal COLLATE Latin1_General_100_BIN2_UTF8; then run out/temporal.sql (sqlcmd -I)
```

## Findings

| Question | Finding | Evidence |
| --- | --- | --- |
| Can a plugin live outside Temporal's repository? | Yes. `sql.RegisterPlugin(name, plugin)` (`common/persistence/sql/store.go`) is public. A Go module of our own can register a `sqlserver` plugin and build a server binary with `temporal.NewServer`. No fork is needed. | `mysql`, `postgresql` and `sqlite` register themselves the same way. |
| How big is a plugin? | The PostgreSQL plugin is 19 files and 4,528 lines; MySQL is 4,588. It implements 157 store methods. | `common/persistence/sql/sqlplugin/{postgresql,mysql}`. |
| What is dialect-specific? | 8 upserts (`ON CONFLICT`, which becomes `MERGE` with `HOLDLOCK`), 15 row locks (`FOR UPDATE` and `FOR SHARE`, which become `WITH (UPDLOCK, ROWLOCK)` and `HOLDLOCK`), 46 `LIMIT` clauses (which become `TOP` or `OFFSET ... FETCH`), 9 `sqlx.In` expansions, and duplicate-key detection (SQL Server errors 2627 and 2601). | grep counts on the PostgreSQL plugin. |
| Does the persistence schema fit SQL Server? | It nearly does. All 37 tables load from a mechanical translation of the MySQL 8 schema. | `translate.py`, run on SQL Server 2022. |
| Key size | With NVARCHAR, four primary keys exceed the 900-byte limit for a clustered key. With VARCHAR on a UTF-8 binary collation (`Latin1_General_100_BIN2_UTF8`), which is one byte per ASCII character and case-sensitive, only `chasm_node_maps` does. Its key (`shard_id`, `namespace_id`, `workflow_id`, `run_id`, `chasm_path VARBINARY(1536)`) is 1,827 bytes. That is over the 1,700-byte limit for a nonclustered key too. A maximum-size row is refused: `Msg 1946 ... index entry of length 1827 bytes ... exceeds the maximum length of 900 bytes`. | Measured. |
| Fix for `chasm_node_maps` | Key on `chasm_path_hash AS CAST(HASHBYTES('SHA2_256', chasm_path) AS BINARY(32)) PERSISTED`. Maximum-size rows then insert, and a duplicate is still refused (`Msg 2627`). The plugin's queries on that table compare the hash and then the path. | Measured. |
| Visibility | It does not translate mechanically. MySQL and PostgreSQL visibility use generated columns over a JSON document, multi-valued and GIN indexes, and a query converter (246 lines on PostgreSQL) that writes the dialect's JSON operators. SQL Server has `JSON_VALUE`, persisted computed columns and `OPENJSON`, but a keyword list (`KeywordList`) has no indexable equivalent of `@>` and needs a side table. | `schema/mysql/v8/visibility/schema.sql` fails to load at `GENERATED`. |
| Proof of a plugin | Temporal's persistence test suites (`common/persistence/tests`, 27 files) run against any registered plugin. A SQL Server plugin is done when it passes them and the conformance kit runs on a server that uses it. | |

## Recommendation

- **Main store: feasible, and a bounded piece of work.** Write `sqlserver` as a port of the PostgreSQL plugin, in a module of our own. Use:
  - the translated schema, with the `chasm_node_maps` hash key;
  - the UTF-8 binary collation;
  - `MERGE ... WITH (HOLDLOCK)` for upserts;
  - lock hints for `FOR UPDATE`;
  - `github.com/microsoft/go-mssqldb`.

  Exit when Temporal's persistence suites pass on SQL Server 2022 and the kit passes on a server built with the plugin (P4).
- **Visibility: do not start on SQL Server.** Temporal lets visibility use a different store from persistence. Keep visibility on PostgreSQL, or on Elasticsearch where a bank has it, until a SQL Server visibility design (computed columns, plus a side table for keyword lists) is written and proved. A bank that must have everything on SQL Server needs that second piece of work.
- **Owner.** We would maintain this plugin on our own, against every Temporal release. Temporal's persistence schema is at its 20th version (v1.0 to v1.19), and the plugin must follow each new one. That cost is the reason to confirm D2 before the port starts.

`translate.py` is a spike tool. Its output is evidence, not the plugin's schema.
