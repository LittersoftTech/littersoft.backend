-- Records (or edits) one party's review of a finished booking. Handles BOTH
-- directions and BOTH booking kinds:
--   @ReviewerType = 'Parent'   -> the pet parent reviewing the provider; @ActorId
--                                 is their PetParentId. Rating + optional comment.
--   @ReviewerType = 'Provider' -> the provider rating the pet parent; @ActorId is
--                                 their ProviderId. Rating only — any comment
--                                 passed in is dropped (the provider-side endpoint
--                                 has no comment field; this only guards a direct
--                                 caller from tripping the table CHECK).
--
-- Deliberately ONE procedure rather than the single-day / night-stay twin pair used
-- elsewhere (cf. [Booking].[MarkBookingPaid] + [Booking].[MarkNightStayBookingPaid]).
-- Those twins exist because the flows genuinely differ — date range vs time window,
-- different grace windows, different codes. Here the ONLY difference is which table
-- supplies the two party ids and the status, so twinning would just be two copies of
-- the same gate to keep in step.
--
-- Submitting again replaces the rating and comment on the SAME row (the endpoint is
-- an upsert), so a corrected star or a fixed typo does not create a second review.
-- Photos are attached separately via [Review].[AddBookingReviewPhoto] — the blob
-- path is keyed by the review id, which does not exist until this runs.
--
-- Returns TWO result sets: the review row, then its photos (empty on first submit).
--
-- THROWs: 51300 booking not found, 51301 caller is not that party to the booking,
-- 51302 booking has not reached COMPLETED / PAID, 51303 Custom walk-in (no
-- pet-parent record to author or receive a review), 51304 invalid argument.
CREATE OR ALTER PROCEDURE [Review].[UpsertBookingReview]
    @BookingType NVARCHAR(16),      -- 'SingleDay' | 'NightStay'
    @BookingId UNIQUEIDENTIFIER,
    @ReviewerType NVARCHAR(16),     -- 'Parent' | 'Provider'
    @ActorId UNIQUEIDENTIFIER,
    @Rating TINYINT,
    @Comment NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive: the API validates all three before calling, so reaching these is
    -- a direct-caller error rather than something a client can provoke.
    IF @BookingType NOT IN (N'SingleDay', N'NightStay')
        OR @ReviewerType NOT IN (N'Parent', N'Provider')
        OR @Rating IS NULL OR @Rating < 1 OR @Rating > 5
    BEGIN
        THROW 51304, 'Invalid review request.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;
    DECLARE @Status NVARCHAR(48);
    DECLARE @Found BIT = 0;
    DECLARE @ExistingId UNIQUEIDENTIFIER;

    -- A provider rates, and says nothing more.
    IF @ReviewerType = N'Provider'
    BEGIN
        SET @Comment = NULL;
    END
    ELSE IF LTRIM(RTRIM(COALESCE(@Comment, N''))) = N''
    BEGIN
        -- Store "no comment written" as NULL, not as an empty string, so a rating
        -- with no words reads back the same whether the field was omitted or blanked.
        SET @Comment = NULL;
    END

    BEGIN TRANSACTION;

    -- No UPDLOCK on the booking, unlike most write paths here: this read cannot go
    -- stale in a way that matters. The only transition out of COMPLETED is to PAID
    -- and PAID is terminal, so once a booking is reviewable it stays reviewable —
    -- a concurrent status change can never invalidate a review we are about to
    -- accept. The lock that DOES matter is the one below, on the review row.
    IF @BookingType = N'SingleDay'
    BEGIN
        SELECT @RowProvider = [ProviderId],
               @RowPetParent = [PetParentId],
               @Status = [Status],
               @Found = 1
        FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SELECT @RowProvider = [ProviderId],
               @RowPetParent = [PetParentId],
               @Status = [Status],
               @Found = 1
        FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @BookingId;
    END

    -- Every THROW below relies on SET XACT_ABORT ON to roll the transaction back,
    -- matching [Booking].[UpdateBookingStatus] and the rest of this codebase.
    IF @Found = 0
    BEGIN
        THROW 51300, 'Booking was not found.', 1;
    END

    -- Custom walk-ins carry free-text customer details and no PetParentId, so
    -- neither direction is possible: there is nobody to author the parent's review
    -- and nobody for the provider to rate. (Night-stay is App-only, so this can
    -- only fire on a single-day booking.)
    IF @RowPetParent IS NULL
    BEGIN
        THROW 51303, 'Only app bookings can be reviewed.', 1;
    END

    IF (@ReviewerType = N'Parent' AND @RowPetParent <> @ActorId)
        OR (@ReviewerType = N'Provider' AND @RowProvider <> @ActorId)
    BEGIN
        THROW 51301, 'You are not a party to this booking.', 1;
    END

    -- PAID counts as well as COMPLETED. PAID sits downstream of COMPLETED, so
    -- gating on COMPLETED alone would close the review window the moment the
    -- provider recorded the payment — which for cash is often immediately.
    IF @Status NOT IN (N'COMPLETED', N'PAID')
    BEGIN
        THROW 51302, 'Booking must be completed before it can be reviewed.', 1;
    END

    -- UPDLOCK + HOLDLOCK over the unique key range: when no row exists yet this
    -- takes a range lock, so two devices submitting at once serialise and the
    -- second updates the first's row instead of hitting a UNIQUE violation.
    SELECT @ExistingId = [BookingReviewId]
    FROM [Review].[BookingReviews] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingType] = @BookingType
      AND [BookingId] = @BookingId
      AND [ReviewerType] = @ReviewerType;

    IF @ExistingId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([BookingReviewId] UNIQUEIDENTIFIER);

        INSERT INTO [Review].[BookingReviews]
            ([BookingType], [BookingId], [ReviewerType], [ProviderId], [PetParentId],
             [Rating], [Comment], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[BookingReviewId] INTO @Inserted
        VALUES
            (@BookingType, @BookingId, @ReviewerType, @RowProvider, @RowPetParent,
             @Rating, @Comment, @Now, @Now);

        SELECT @ExistingId = [BookingReviewId] FROM @Inserted;
    END
    ELSE
    BEGIN
        -- CreatedAtUtc is left alone: it is the date the review was GIVEN, which is
        -- what the list sorts on and what the app shows. An edit is not a new review.
        UPDATE [Review].[BookingReviews]
        SET [Rating] = @Rating,
            [Comment] = @Comment,
            [UpdatedAtUtc] = @Now
        WHERE [BookingReviewId] = @ExistingId;
    END

    SELECT [BookingReviewId],
           [BookingType],
           [BookingId],
           [ReviewerType],
           [ProviderId],
           [PetParentId],
           [Rating],
           [Comment],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Review].[BookingReviews]
    WHERE [BookingReviewId] = @ExistingId;

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewId] = @ExistingId
    ORDER BY [CreatedAtUtc] ASC, [BookingReviewPhotoId] ASC;

    COMMIT TRANSACTION;
END;
