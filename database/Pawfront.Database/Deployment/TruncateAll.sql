/*
================================================================================
  Pawfront — truncate (empty) the entire SQL database
--------------------------------------------------------------------------------
  Deletes ALL rows from EVERY table in the Provider, Parent, Event, and Booking
  schemas (plus the retired Customer schema, if an old deployment still has it)
  and reseeds every identity column, leaving the structure (tables, indexes, FKs,
  stored procedures) fully intact. The end state is identical to a freshly
  deployed, empty SQL database.

  ** THIS IS DESTRUCTIVE AND IRREVERSIBLE. ALL APPLICATION DATA IS LOST. **
  Never run this against a production database.

  Usage:
      sqlcmd -S <server> -d <database> -U <user> -P <password> -i TruncateAll.sql
      (or paste into SSMS / Azure Data Studio and Execute)

  Safety guard:
      The script aborts unless you flip @IAmSure to 1 below. This is a
      deliberate guard against accidental execution.

  ------------------------------------------------------------------------------
  ** SQL IS ONLY ONE OF THREE STORES — this script empties SQL ONLY. **
  ------------------------------------------------------------------------------
  Pawfront keeps application state in three places. Emptying SQL alone does NOT
  return the system to a fresh state:

    * Cosmos DB (database `pawfront`)
        - container `ProviderServices` (partition /serviceCategory) — the
          per-category service listing. Parent-facing discovery
          (GET /providers, and all five /providers/search/* endpoints) reads
          Cosmos, so every provider whose SQL row you just deleted WILL STILL
          APPEAR in browse results until these documents are removed.
        - container `Events` (partition /eventCategory) — physical-event
          capacity + venue location.
    * Blob Storage (container `provider-images`) — profile / service / event /
      pet / parent / identity / booking-evidence / banner images. Orphaned blobs
      are invisible to the app once their SQL row is gone, but they still cost
      storage.

  Clear those two separately (portal, Storage Explorer, or `az` CLI) whenever you
  want a genuinely clean slate. The reminder is reprinted at the end of the run.

  ------------------------------------------------------------------------------
  How it works
  ------------------------------------------------------------------------------
    * Self-maintaining — discovers tables dynamically from sys.tables, so newly
      added tables in these schemas are covered automatically. There is NO table
      list to keep in sync with DeployAll.sql.
    * Uses DELETE (not TRUNCATE): TRUNCATE is disallowed on tables referenced by
      a FOREIGN KEY even when the constraint is disabled. DELETE + identity
      reseed reaches the same empty state for every table.
    * Disables all FK constraints first so rows can be removed in any order,
      then re-enables (WITH CHECK) afterwards so integrity is re-validated.
    * Reseeds each identity column from its own declared seed/increment
      (sys.identity_columns), so e.g. Booking.Bookings.JobNumber — the friendly
      `PF-000123` job id — restarts at 1.
    * Verifies every target table is actually empty BEFORE committing, and
      aborts the whole run if anything survived.
    * Wrapped in a single transaction with XACT_ABORT — any failure rolls the
      whole thing back, so you never end up half-emptied.
    * Reports per-table row counts before deleting, so the run is auditable.
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
-- [Customer] is the retired pet-parent schema: DeployAll.sql transfers its
-- tables to [Parent] and drops it, so it normally matches nothing. Listed here
-- so this script still fully empties a database that predates that migration.
DECLARE @Schemas TABLE ([name] SYSNAME PRIMARY KEY);
INSERT INTO @Schemas ([name])
VALUES (N'Provider'), (N'Parent'), (N'Event'), (N'Booking'), (N'Customer');

-- Resolve the target tables ONCE, so every step below operates on exactly the
-- same set.
DECLARE @Targets TABLE
(
    [object_id]  INT      NOT NULL PRIMARY KEY,
    [SchemaName] SYSNAME  NOT NULL,
    [TableName]  SYSNAME  NOT NULL
);

INSERT INTO @Targets ([object_id], [SchemaName], [TableName])
SELECT t.[object_id], s.[name], t.[name]
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] IN (SELECT [name] FROM @Schemas)
  AND t.[is_ms_shipped] = 0;

IF NOT EXISTS (SELECT 1 FROM @Targets)
BEGIN
    RAISERROR(
        'TruncateAll aborted: no tables found in the Provider/Parent/Event/Booking schemas. Wrong database?',
        16, 1);
    RETURN;
END

DECLARE @sql        NVARCHAR(MAX);
DECLARE @TableCount INT = (SELECT COUNT(*) FROM @Targets);

PRINT '--- Pawfront TruncateAll starting ---';
PRINT '  Target tables: ' + CAST(@TableCount AS VARCHAR(10));

--------------------------------------------------------------------------------
-- 1. Pre-flight report — what is about to be deleted.
--    Row counts come from sys.dm_db_partition_stats (instant; approximate for
--    heaps). The post-run verification below uses exact COUNT_BIG(*).
--------------------------------------------------------------------------------
SELECT
    tg.[SchemaName],
    tg.[TableName],
    SUM(ps.[row_count]) AS [RowsBeforeDelete]
FROM @Targets AS tg
JOIN sys.dm_db_partition_stats AS ps
      ON ps.[object_id] = tg.[object_id]
     AND ps.[index_id] IN (0, 1)   -- heap or clustered index only
GROUP BY tg.[SchemaName], tg.[TableName]
ORDER BY tg.[SchemaName], tg.[TableName];

BEGIN TRANSACTION;

--------------------------------------------------------------------------------
-- 2. Disable every foreign key constraint on the target tables.
--------------------------------------------------------------------------------
SET @sql = (
    SELECT STRING_AGG(
               CAST(N'ALTER TABLE ' + QUOTENAME([SchemaName]) + N'.' + QUOTENAME([TableName])
                    + N' NOCHECK CONSTRAINT ALL;' AS NVARCHAR(MAX)),
               CHAR(10))
    FROM @Targets);

EXEC sys.sp_executesql @sql;
PRINT '  FK constraints disabled.';

--------------------------------------------------------------------------------
-- 3. Delete all rows from every target table.
--------------------------------------------------------------------------------
SET @sql = (
    SELECT STRING_AGG(
               CAST(N'DELETE FROM ' + QUOTENAME([SchemaName]) + N'.' + QUOTENAME([TableName])
                    + N';' AS NVARCHAR(MAX)),
               CHAR(10))
    FROM @Targets);

EXEC sys.sp_executesql @sql;
PRINT '  All rows deleted.';

--------------------------------------------------------------------------------
-- 4. Reseed identity columns so new rows start at the declared seed again.
--    RESEED takes (seed - increment) so the NEXT value handed out is the seed
--    itself — e.g. IDENTITY(1,1) is reseeded to 0 and the next row gets 1.
--------------------------------------------------------------------------------
SET @sql = (
    SELECT STRING_AGG(
               CAST(N'DBCC CHECKIDENT (N'''
                    + REPLACE(tg.[SchemaName], N'''', N'''''') + N'.'
                    + REPLACE(tg.[TableName],  N'''', N'''''')
                    + N''', RESEED, '
                    + CAST(CAST(ic.[seed_value] AS BIGINT)
                           - CAST(ic.[increment_value] AS BIGINT) AS NVARCHAR(20))
                    + N') WITH NO_INFOMSGS;' AS NVARCHAR(MAX)),
               CHAR(10))
    FROM @Targets AS tg
    JOIN sys.identity_columns AS ic ON ic.[object_id] = tg.[object_id]);

IF @sql IS NOT NULL
    EXEC sys.sp_executesql @sql;
PRINT '  Identity columns reseeded.';

--------------------------------------------------------------------------------
-- 5. Re-enable (and re-validate) every foreign key constraint.
--------------------------------------------------------------------------------
SET @sql = (
    SELECT STRING_AGG(
               CAST(N'ALTER TABLE ' + QUOTENAME([SchemaName]) + N'.' + QUOTENAME([TableName])
                    + N' WITH CHECK CHECK CONSTRAINT ALL;' AS NVARCHAR(MAX)),
               CHAR(10))
    FROM @Targets);

EXEC sys.sp_executesql @sql;
PRINT '  FK constraints re-enabled.';

--------------------------------------------------------------------------------
-- 6. Verify every target table is genuinely empty BEFORE committing.
--    Exact COUNT_BIG(*) per table; anything left over aborts the run and rolls
--    the whole thing back (XACT_ABORT + THROW), so a partial empty can't commit.
--------------------------------------------------------------------------------
DECLARE @Remaining TABLE
(
    [SchemaName]    SYSNAME NOT NULL,
    [TableName]     SYSNAME NOT NULL,
    [RemainingRows] BIGINT  NOT NULL
);

SET @sql = (
    SELECT STRING_AGG(
               CAST(N'SELECT N''' + REPLACE([SchemaName], N'''', N'''''') + N''''
                    + N', N''' + REPLACE([TableName], N'''', N'''''') + N''''
                    + N', COUNT_BIG(*) FROM '
                    + QUOTENAME([SchemaName]) + N'.' + QUOTENAME([TableName]) AS NVARCHAR(MAX)),
               CHAR(10) + N'UNION ALL ')
    FROM @Targets);

INSERT INTO @Remaining ([SchemaName], [TableName], [RemainingRows])
EXEC sys.sp_executesql @sql;

IF EXISTS (SELECT 1 FROM @Remaining WHERE [RemainingRows] > 0)
BEGIN
    SELECT [SchemaName], [TableName], [RemainingRows]
    FROM @Remaining
    WHERE [RemainingRows] > 0
    ORDER BY [SchemaName], [TableName];

    THROW 52000,
          'TruncateAll aborted: one or more tables still contain rows after the delete pass. Nothing was committed.',
          1;
END

PRINT '  Verified: every target table is empty.';

COMMIT TRANSACTION;

PRINT '--- Pawfront TruncateAll complete: the SQL database is now empty ---';
PRINT '';
PRINT '  REMINDER — SQL is only one of three stores. Still to clear manually:';
PRINT '    * Cosmos DB `pawfront`: containers [ProviderServices] and [Events].';
PRINT '      Parent-facing discovery reads Cosmos, so providers deleted here will';
PRINT '      STILL appear in GET /providers until their documents are removed.';
PRINT '    * Blob Storage: container [provider-images] (all folders).';
GO
