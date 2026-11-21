#!/usr/bin/env python3
"""P4 spike: translate Temporal's MySQL 8 schema (persistence and visibility) to T-SQL for SQL Server 2022.

usage: translate.py <temporal-server-source-dir> <out-dir>

The MySQL schema is the starting point because MySQL, like SQL Server, limits index key size, so its
key columns are already sized (BINARY(16) ids, VARBINARY(272) task queue ids). The output is for
measuring what the schema needs on SQL Server, not a finished plugin schema: see README.md.
"""
import re, sys, pathlib

TYPES = [
    (r'\bMEDIUMBLOB\b|\bLONGBLOB\b|\bBLOB\b', 'VARBINARY(MAX)'),
    (r'\bMEDIUMTEXT\b|\bLONGTEXT\b|\bTEXT\b', 'NVARCHAR(MAX)'),
    (r'\bDATETIME\(6\)', 'DATETIME2(6)'),
    (r'\bTIMESTAMP\b', 'DATETIME2(6)'),
    (r'\bTINYINT\(1\)', 'BIT'),
    (r'\bBOOLEAN\b', 'BIT'),
    (r'\bINT UNSIGNED\b', 'BIGINT'),          # SQL Server has no unsigned INT
    (r'\bBIGINT UNSIGNED\b', 'DECIMAL(20,0)'),
    (r'\bJSON\b', 'NVARCHAR(MAX)'),           # validated with ISJSON, read with JSON_VALUE / OPENJSON
    (r'\bAUTO_INCREMENT\b', 'IDENTITY(1,1)'),
]

def tsql(sql: str) -> str:
    sql = re.sub(r'--[^\n]*', '', sql)
    for pat, rep in TYPES:
        sql = re.sub(pat, rep, sql, flags=re.I)
    # VARCHAR stays VARCHAR: the database is created with a UTF-8 binary collation
    # (Latin1_General_100_BIN2_UTF8, SQL Server 2019+), so ids keep one byte per ASCII character and
    # compare case-sensitively, as Temporal expects. NVARCHAR would double every key column.
    if '--nvarchar' in sys.argv:
        sql = re.sub(r'\bVARCHAR\((\d+)\)', r'NVARCHAR(\1)', sql)
    # `name NVARCHAR(255) UNIQUE NOT NULL` is fine in T-SQL; `IDENTITY ... UNIQUE` too.
    sql = re.sub(r'\s+ENGINE\s*=\s*\w+', '', sql, flags=re.I)
    sql = re.sub(r'\s+(DEFAULT\s+)?CHARSET\s*=\s*\w+', '', sql, flags=re.I)
    sql = re.sub(r'\s+COLLATE\s*=?\s*\w+', '', sql, flags=re.I)
    sql = sql.replace('`', '')
    # MySQL inline `INDEX name (cols)` inside CREATE TABLE is T-SQL `INDEX name NONCLUSTERED (cols)`.
    sql = re.sub(r'^(\s*)(UNIQUE\s+)?(INDEX|KEY)\s+(\w+)\s*\(', lambda m: f"{m.group(1)}INDEX {m.group(4)} {'UNIQUE ' if m.group(2) else ''}NONCLUSTERED (", sql, flags=re.M)
    sql = re.sub(r'\bINDEX (\w+) UNIQUE NONCLUSTERED', r'CONSTRAINT \1 UNIQUE NONCLUSTERED', sql)
    # T-SQL needs a name for an inline index; MySQL lets it be anonymous.
    def name_indexes(m):
        table, n = m.group(1), [0]
        def one(i):
            n[0] += 1
            return f'{i.group(1)}INDEX ix_{table}_{n[0]} NONCLUSTERED ('
        return re.sub(r'^(\s*)INDEX\s*\(', one, m.group(0), flags=re.M)
    sql = re.sub(r'CREATE TABLE (\w+)\s*\(.*?\n\);', name_indexes, sql, flags=re.S)
    sql = re.sub(r'\bIF NOT EXISTS\b', '', sql)
    return sql

def main():
    src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    for part in ['temporal', 'visibility']:
        text = (src / 'schema' / 'mysql' / 'v8' / part / 'schema.sql').read_text()
        (out / f'{part}.sql').write_text(tsql(text))
    print(f'wrote {out}/temporal.sql and {out}/visibility.sql')

if __name__ == '__main__':
    main()
