-- The laptop stack's logins on PostgreSQL 16 (compose.postgres.yaml, R-09-140), the same as sql/grants.sql gives on SQL Server
-- (R-09-81). Run once by db-grants as the server's owner (postgres), after the migrate step. Passwords come from psql variables, set
-- from the environment; nothing here is a secret.
--
-- th_runtime: every runtime host. In the store (th_store) it may read and insert, and nothing more: UPDATE, DELETE and TRUNCATE
-- are revoked on every table, it owns no table (so it may not ALTER, DROP or TRUNCATE one) and may not create in the schema. On
-- PostgreSQL a right not granted is denied, so the revokes say what SQL Server's DENY says. In the read model (th_ops), which the
-- projector rebuilds, it reads and writes.
-- th_testbench: the test bench's engine runs, each on a database of its own that it creates (test data only).
-- th_temporal: the Temporal server, data rights in its two databases only, no schema rights, no superuser.
\set ON_ERROR_STOP on
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', r, p) FROM (VALUES ('th_runtime', :'runtime_password'), ('th_testbench', :'testbench_password'), ('th_temporal', :'temporal_password')) AS v(r, p)
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) \gexec
ALTER ROLE th_runtime WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'runtime_password';
ALTER ROLE th_testbench WITH LOGIN NOSUPERUSER CREATEDB NOCREATEROLE PASSWORD :'testbench_password';
ALTER ROLE th_temporal WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'temporal_password';

\connect th_store
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE th_store TO th_runtime;
GRANT USAGE ON SCHEMA public TO th_runtime;
REVOKE CREATE ON SCHEMA public FROM th_runtime;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA public TO th_runtime;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA public FROM th_runtime;
-- Identity columns draw from sequences: reading the next value is part of an insert.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO th_runtime;

\connect th_ops
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE th_ops TO th_runtime;
GRANT USAGE ON SCHEMA public TO th_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO th_runtime;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO th_runtime;

\connect temporal
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE temporal TO th_temporal;
GRANT USAGE ON SCHEMA public TO th_temporal;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO th_temporal;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO th_temporal;

\connect temporal_visibility
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE temporal_visibility TO th_temporal;
GRANT USAGE ON SCHEMA public TO th_temporal;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO th_temporal;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO th_temporal;

\echo 'grants applied'
