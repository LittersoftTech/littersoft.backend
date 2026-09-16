-- Blocks the counterparty, in the caller's direction.
--
-- Idempotent: blocking somebody already blocked returns the existing row rather
-- than failing or duplicating, so a double-tap is harmless. [WasAlreadyBlocked]
-- says which happened.
--
-- A block severs the pair across the product from this one row -- messages,
-- new bookings, sight of each other's events, and the provider's place in browse
-- and all five searches. See [Block].[BlockedParticipants] for the full policy and
-- for what a block deliberately does NOT do.
--
-- Returns TWO result sets:
--   1. the block row, plus [WasAlreadyBlocked].
--   2. the pair's UNFINISHED jobs, which the caller then cancels one by one
--      through the ordinary status transition -- so each gets its party check,
--      its audit row, its freed capacity and its counterparty notification for
--      free, exactly as [IBulkBookingCancellationService] does.
--
-- WHY THE JOBS ARE RETURNED RATHER THAN CANCELLED HERE: cancelling in T-SQL would
-- mean duplicating the whole status engine (from-state rules, audit, capacity,
-- the notification enqueue) inside this procedure, and the two copies would drift.
-- The list is captured inside the transaction that writes the block, so a booking
-- cannot be created between the block landing and the list being taken --
-- [Booking].[CreateBooking] reads this table under HOLDLOCK and therefore
-- serialises against the INSERT below.
--
-- WHAT IS DELIBERATELY NOT IN THAT LIST:
--   * IN_PROGRESS / ENDING -- the pet is physically in someone's care and
--     [Booking].[UpdateBookingStatus] refuses the cancel (THROW 51149). That guard
--     is not bypassed; such a job runs to completion carrying the blocked flag.
--   * COMPLETED / PAID and every terminal status -- nothing to cancel. (PAID is
--     named explicitly: the engine's own terminal list omits it, since PAID is
--     reachable only from COMPLETED, which is terminal.)
--   * a CREATED booking that has ALREADY expired under BR-17 (24h unanswered) or
--     BR-53 (under 2h to the service). The engine rejects every transition on
--     those, cancel included (THROW 51129 / 51153), so listing them would produce
--     a guaranteed per-item failure for a booking that is already dead and merely
--     waiting for the sweep to label it EXPIRED.
--
-- THROWs: 51327 the two parties are on the same side (a blockable relationship
-- only ever runs provider <-> parent, so such a block could never be consulted).
CREATE OR ALTER PROCEDURE [Block].[BlockParticipant]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @BlockedType NVARCHAR(16),
    @BlockedId UNIQUEIDENTIFIER,
    @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BlockerType NOT IN (N'Provider', N'PetParent')
        OR @BlockedType NOT IN (N'Provider', N'PetParent')
        OR @BlockerType = @BlockedType
    BEGIN
        THROW 51327, 'A block must run between a provider and a pet parent.', 1;
    END

    IF LTRIM(RTRIM(COALESCE(@Reason, N''))) = N''
    BEGIN
        SET @Reason = NULL;
    END

    -- Which side is which. Everything below is expressed in terms of the pair, not
    -- of who did the blocking, so one query serves both directions.
    DECLARE @ProviderId UNIQUEIDENTIFIER =
        CASE WHEN @BlockerType = N'Provider' THEN @BlockerId ELSE @BlockedId END;
    DECLARE @PetParentId UNIQUEIDENTIFIER =
        CASE WHEN @BlockerType = N'PetParent' THEN @BlockerId ELSE @BlockedId END;

    DECLARE @BlockId UNIQUEIDENTIFIER;
    DECLARE @WasAlreadyBlocked BIT = 0;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK over the unique 4-tuple: with no row yet this takes a
    -- range lock, so two concurrent blocks serialise instead of one hitting a
    -- UNIQUE violation -- and a concurrent booking create, which reads this same
    -- range under HOLDLOCK, serialises behind it too.
    SELECT @BlockId = [BlockId]
    FROM [Block].[BlockedParticipants] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId
      AND [BlockedType] = @BlockedType
      AND [BlockedId] = @BlockedId;

    IF @BlockId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([BlockId] UNIQUEIDENTIFIER);

        INSERT INTO [Block].[BlockedParticipants]
            ([BlockerType], [BlockerId], [BlockedType], [BlockedId], [Reason], [CreatedAtUtc])
        OUTPUT inserted.[BlockId] INTO @Inserted
        VALUES
            (@BlockerType, @BlockerId, @BlockedType, @BlockedId, @Reason, @Now);

        SELECT @BlockId = [BlockId] FROM @Inserted;
    END
    ELSE
    BEGIN
        SET @WasAlreadyBlocked = 1;
    END

    -- Result set 1: the block row.
    SELECT [BlockId],
           [BlockerType],
           [BlockerId],
           [BlockedType],
           [BlockedId],
           [Reason],
           [CreatedAtUtc],
           -- CAST is not decoration: COALESCE/CASE over a BIT and an INT literal
           -- yields INT by data-type precedence, and SqlDataReader.GetBoolean does
           -- not coerce -- it throws, after this transaction has committed.
           CAST(@WasAlreadyBlocked AS BIT) AS [WasAlreadyBlocked]
    FROM [Block].[BlockedParticipants]
    WHERE [BlockId] = @BlockId;

    -- Result set 2: the unfinished jobs for the pair, soonest first, both kinds.
    -- Empty on a repeat block -- the first one already cancelled them.
    SELECT [BookingType],
           [BookingId],
           [JobNumber],
           [Status],
           [ServiceDate]
    FROM (
        SELECT N'SingleDay' AS [BookingType],
               b.[BookingId] AS [BookingId],
               b.[JobNumber] AS [JobNumber],
               b.[Status] AS [Status],
               b.[BookingDate] AS [ServiceDate]
        FROM [Booking].[Bookings] b
        WHERE b.[ProviderId] = @ProviderId
          AND b.[PetParentId] = @PetParentId
          AND b.[Status] NOT IN (N'COMPLETED', N'PAID', N'IN_PROGRESS', N'ENDING',
                                 N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                 N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED',
                                 N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          -- Already dead under BR-17 / BR-53; the engine would reject the cancel.
          AND NOT (
                b.[Status] = N'CREATED'
                AND (
                    @Now >= DATEADD(HOUR, 24, b.[CreatedAtUtc])
                    OR DATEADD(SECOND,
                               DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                               CAST(b.[BookingDate] AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
                )
          )

        UNION ALL

        SELECT N'NightStay' AS [BookingType],
               n.[NightStayBookingId] AS [BookingId],
               n.[JobNumber] AS [JobNumber],
               n.[Status] AS [Status],
               n.[CheckInDate] AS [ServiceDate]
        FROM [Booking].[NightStayBookings] n
        WHERE n.[ProviderId] = @ProviderId
          AND n.[PetParentId] = @PetParentId
          AND n.[Status] NOT IN (N'COMPLETED', N'PAID', N'IN_PROGRESS', N'ENDING',
                                 N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                 N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED',
                                 N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND NOT (
                n.[Status] = N'CREATED'
                AND (
                    @Now >= DATEADD(HOUR, 24, n.[CreatedAtUtc])
                    OR DATEADD(SECOND,
                               DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                               CAST(n.[CheckInDate] AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
                )
          )
    ) AS [Jobs]
    ORDER BY [ServiceDate] ASC, [BookingId] ASC;

    COMMIT TRANSACTION;
END;
