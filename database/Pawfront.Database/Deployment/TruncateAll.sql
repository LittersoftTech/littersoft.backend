/*
================================================================================
  Pawfront — truncate (empty) the entire database
--------------------------------------------------------------------------------
  Deletes ALL rows from EVERY table in the Provider, Parent, Event, and Booking
  schemas and reseeds every identity column, leaving the structure (tables,
  indexes, FKs, stored procedures) fully intact. The end state is identical to a
  freshly deployed, empty database.

  ** THIS IS DESTRUCTIVE AND IRREVERSIBLE. ALL APPLICATION DATA IS LOST. **
  Never run this against a production database.

  Usage:
      sqlcmd -S <server> -d <database> -U <user> -P <password> -i TruncateAll.sql
      (or paste into SSMS / Azure Data Studio and Execute)

  Safety guard:
      The script aborts unless you flip @IAmSure to 1 below. This is a
      deliberate guard against accidental execution.

  How it works:
    * Self-maintaining — discovers tables dynamically from sys.tables, so newly
      added tables in these schemas are covered automatically.
    * Uses DELETE (not TRUNCATE): TRUNCATE is disallowed on tables referenced by
      a FOREIGN KEY even when the constraint is disabled. DELETE + identity
      reseed reaches the same empty state for every table.
    * Disables all FK constraints first so rows can be removed in any order,
      then re-enables (WITH CHECK) afterwards so integrity is re-validated.
    * Wrapped in a single transaction with XACT_ABORT — any failure rolls the
      whole thing back, so you never end up half-emptied.
================================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

--------------------------------------------------------------------------------
-- 0. Safety guard — set to 1 to actually run.
--------------------------------------------------------------------------------
DECLARE @IAmSure BIT = 0;

IF @IAmSure <> 1
BEGIN
    RAISERROR(
        'TruncateAll aborted: set @IAmSure = 1 to confirm you want to delete ALL data.',
        16, 1);
    RETURN;
END

-- The schemas whose tables should be emptied.
DECLARE @Schemas TABLE ([name] SYSNAME PRIMARY KEY);
INSERT INTO @Schemas ([name]) VALUES (N'Provider'), (N'Parent'), (N'Event'), (N'Booking');

DECLARE @sql NVARCHAR(MAX);

PRINT '--- Pawfront TruncateAll starting ---';

BEGIN TRANSACTION;

--------------------------------------------------------------------------------
-- 1. Disable every foreign key constraint on the target tables.
--------------------------------------------------------------------------------
SET @sql = N'';
SELECT @sql = @sql
     + N'ALTER TABLE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name])
     + N' NOCHECK CONSTRAINT ALL;' + CHAR(10)
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] IN (SELECT [name] FROM @Schemas);

EXEC sys.sp_executesql @sql;
PRINT '  FK constraints disabled.';

--------------------------------------------------------------------------------
-- 2. Delete all rows from every target table.
--------------------------------------------------------------------------------
SET @sql = N'';
SELECT @sql = @sql
     + N'DELETE FROM ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name]) + N';' + CHAR(10)
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] IN (SELECT [name] FROM @Schemas);

EXEC sys.sp_executesql @sql;
PRINT '  All rows deleted.';

--------------------------------------------------------------------------------
-- 3. Reseed identity columns so new rows start at 1 again.
--------------------------------------------------------------------------------
SET @sql = N'';
SELECT @sql = @sql
     + N'DBCC CHECKIDENT (''' + s.[name] + N'.' + t.[name]
     + N''', RESEED, 0) WITH NO_INFOMSGS;' + CHAR(10)
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] IN (SELECT [name] FROM @Schemas)
  AND EXISTS (SELECT 1 FROM sys.identity_columns ic WHERE ic.[object_id] = t.[object_id]);

IF LEN(@sql) > 0
    EXEC sys.sp_executesql @sql;
PRINT '  Identity columns reseeded.';

--------------------------------------------------------------------------------
-- 4. Re-enable (and re-validate) every foreign key constraint.
--------------------------------------------------------------------------------
SET @sql = N'';
SELECT @sql = @sql
     + N'ALTER TABLE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name])
     + N' WITH CHECK CHECK CONSTRAINT ALL;' + CHAR(10)
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] IN (SELECT [name] FROM @Schemas);

EXEC sys.sp_executesql @sql;
PRINT '  FK constraints re-enabled.';

COMMIT TRANSACTION;

PRINT '--- Pawfront TruncateAll complete: database is now empty ---';
GO
