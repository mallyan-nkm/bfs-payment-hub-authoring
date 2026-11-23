-- The laptop stack's logins and what each may do (deploy/laptop, R-09-78). Run once by db-grants as the server's owner, after
-- the migrations. Passwords come from sqlcmd variables, set from the environment; nothing here is a secret.
--
-- th_runtime: every runtime host. In the store (th_store) it may read and insert, and is denied UPDATE, DELETE and ALTER on
-- the whole schema: a distribution, its definitions (the package it carries), its activations and the fact log are
-- inserted, never updated or deleted, by the database's permissions as well as by the store's guard (plan W2). TRUNCATE needs
-- ALTER, which is denied too. In the read model (th_ops), which the projector rebuilds, it reads and writes.
-- th_testbench: the test bench's engine runs, each on a database of its own that it creates (test data only).
-- th_temporal: the Temporal server, data and EXECUTE in its two databases only, no server role (runtime/temporal-mssql).
SET NOCOUNT ON;
IF SUSER_ID('th_runtime') IS NULL EXEC('CREATE LOGIN th_runtime WITH PASSWORD = ''$(RUNTIME_PASSWORD)'', CHECK_POLICY = OFF');
ELSE EXEC('ALTER LOGIN th_runtime WITH PASSWORD = ''$(RUNTIME_PASSWORD)''');
IF SUSER_ID('th_testbench') IS NULL EXEC('CREATE LOGIN th_testbench WITH PASSWORD = ''$(TESTBENCH_PASSWORD)'', CHECK_POLICY = OFF');
ELSE EXEC('ALTER LOGIN th_testbench WITH PASSWORD = ''$(TESTBENCH_PASSWORD)''');
IF SUSER_ID('th_temporal') IS NULL EXEC('CREATE LOGIN th_temporal WITH PASSWORD = ''$(TEMPORAL_PASSWORD)'', CHECK_POLICY = OFF');
ELSE EXEC('ALTER LOGIN th_temporal WITH PASSWORD = ''$(TEMPORAL_PASSWORD)''');
ALTER SERVER ROLE dbcreator ADD MEMBER th_testbench;
GO
USE th_store;
IF USER_ID('th_runtime') IS NULL CREATE USER th_runtime FOR LOGIN th_runtime;
GRANT SELECT, INSERT ON SCHEMA::dbo TO th_runtime;
-- (Not CONTROL: a DENY of CONTROL would deny SELECT and INSERT too.)
DENY UPDATE, DELETE, ALTER ON SCHEMA::dbo TO th_runtime;
DENY ALTER ANY SCHEMA, CREATE TABLE, CREATE PROCEDURE, CREATE FUNCTION, CREATE VIEW TO th_runtime;
GO
USE th_ops;
IF USER_ID('th_runtime') IS NULL CREATE USER th_runtime FOR LOGIN th_runtime;
ALTER ROLE db_datareader ADD MEMBER th_runtime;
ALTER ROLE db_datawriter ADD MEMBER th_runtime;
GO
USE temporal;
IF USER_ID('th_temporal') IS NULL CREATE USER th_temporal FOR LOGIN th_temporal;
ALTER ROLE db_datareader ADD MEMBER th_temporal;
ALTER ROLE db_datawriter ADD MEMBER th_temporal;
GRANT EXECUTE TO th_temporal;
GO
USE temporal_visibility;
IF USER_ID('th_temporal') IS NULL CREATE USER th_temporal FOR LOGIN th_temporal;
ALTER ROLE db_datareader ADD MEMBER th_temporal;
ALTER ROLE db_datawriter ADD MEMBER th_temporal;
GRANT EXECUTE TO th_temporal;
GO
PRINT 'grants applied';
