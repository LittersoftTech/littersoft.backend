-- Builds the standard EVENT notification payload and enqueues it. The event twin
-- of [Notification].[EnqueueBookingNotification], and for the same reason: ONE
-- place assembles the `data` object for this category, so the mobile contract
-- can't drift between call sites.
--
-- Recipient is always the event's ORGANISER, which is why no @Audience parameter
-- is needed: [Event].[Events] carries exactly one of [ProviderId] / [PetParentId]
-- (a CHECK enforces it), and that choice IS the audience. The BUYER is
-- deliberately not addressable here — [Event].[EventBookings] identifies its
-- booker only by free-text [BookerEmail], with no FK to either user table, so
-- there is no id to send a push to. Buyer-facing event notifications need a
-- persisted booker id first.
--
-- Canonical id block for this category: eventId is populated, bookingId and
-- isNightStay are left absent (the payload builder emits them as empty strings),
-- and parentId / providerId carry the ORGANISER — whichever they are.
--
-- Never THROWs: a notification must never roll back the ticket transaction that
-- caused it. An unknown @EventId simply enqueues nothing.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueEventNotification]
    @EventId UNIQUEIDENTIFIER,
    @EventBookingId UNIQUEIDENTIFIER = NULL,
    @NotificationType NVARCHAR(64),
    -- The person who bought or cancelled the tickets. Free text off the booking
    -- row, since that is all the schema records about them.
    @BookerName NVARCHAR(200) = NULL,
    @TicketCount INT = NULL,
    @DedupeSuffix NVARCHAR(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @EventTitle NVARCHAR(200);

    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @EventTitle = [Title]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    IF @ProviderId IS NULL AND @PetParentId IS NULL
    BEGIN
        -- Unknown event, or an organiser-less row that should not exist.
        -- Nothing to say, and throwing would take the caller's transaction down.
        RETURN;
    END

    DECLARE @Audience NVARCHAR(16) =
        CASE WHEN @ProviderId IS NOT NULL THEN N'Provider' ELSE N'PetParent' END;
    DECLARE @RecipientId UNIQUEIDENTIFIER = COALESCE(@ProviderId, @PetParentId);

    DECLARE @DataJson NVARCHAR(MAX) =
    (
        SELECT
            -- --- the canonical id block ---
            N'EVENT'                                  AS [category],
            CAST(@EventId AS NVARCHAR(36))            AS [eventId],
            CAST(@PetParentId AS NVARCHAR(36))        AS [parentId],
            CAST(@ProviderId AS NVARCHAR(36))         AS [providerId],
            -- --- template parameters ---
            CAST(@EventBookingId AS NVARCHAR(36))     AS [eventBookingId],
            @EventTitle                               AS [eventTitle],
            -- The copy says "{parentName} booked N tickets"; for an event the
            -- booker may be a provider or a parent, so this is simply whoever
            -- bought them, by name.
            @BookerName                               AS [parentName],
            CAST(@TicketCount AS NVARCHAR(16))        AS [ticketCount]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    DECLARE @DedupeKey NVARCHAR(200) =
        @NotificationType + N':' + CAST(COALESCE(@EventBookingId, @EventId) AS NVARCHAR(36))
        + CASE WHEN @DedupeSuffix IS NULL THEN N'' ELSE N':' + @DedupeSuffix END;

    EXEC [Notification].[EnqueueNotification]
        @Audience = @Audience,
        @RecipientId = @RecipientId,
        @NotificationType = @NotificationType,
        @EntityType = N'EventBooking',
        @EntityId = @EventBookingId,
        @DataJson = @DataJson,
        @DedupeKey = @DedupeKey,
        -- The callers return their own result sets; a nested one would corrupt them.
        @SuppressResultSet = 1;
END
