-- Attaches one photo to a ticket that can carry evidence — everything except a
-- chat incident.
--
-- Scoped to the ticket's CREATOR, not to either party. The evidence is the
-- reporter's account of what happened, and the status vocabulary agrees — support
-- asks for clarification of the CREATOR and receives it from the CREATOR, so the
-- counterparty is never in the evidence loop.
--
-- The 5-photo cap is counted under UPDLOCK + HOLDLOCK because it is a race: two
-- uploads in flight would each read "room for one more". The endpoint pre-checks
-- it as well, so a caller already at the cap is not charged an upload first, but
-- this is the check that actually holds.
--
-- Returns TWO result sets: the ticket row, then ALL its photos oldest-first —
-- the same shape [Support].[GetTicket] returns, so the caller re-renders from one
-- mapping.
--
-- THROWs: 51344 ticket not found for this creator, 51345 already at the cap,
-- 51346 chat incident (carries no photos), 51347 ticket is closed.
CREATE OR ALTER PROCEDURE [Support].[AddTicketPhoto]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MaxPhotos INT = 5;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @TicketType NVARCHAR(24);
    DECLARE @Status NVARCHAR(48);
    DECLARE @PhotoCount INT;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK on the ticket row: it is what the photo count below is
    -- taken against, and it also serialises against a concurrent close.
    SELECT @TicketType = [TicketType],
           @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId
      AND [RaisedByType] = @ActorType
      AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId));

    -- Unknown ticket and "not the one you raised" are one answer, so neither can
    -- be probed (the posture [Chat].[UnblockChatParticipant] and
    -- [Review].[AddBookingReviewPhoto] both take).
    IF @TicketType IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @TicketType = N'ChatIncident'
    BEGIN
        -- The one kind that carries no photos: the images already in the thread
        -- are the evidence, and the whole conversation is under legal hold.
        -- Booking incidents, event incidents and app issues all take them.
        THROW 51346, 'A reported chat cannot carry photos.', 1;
    END

    IF @Status = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    SELECT @PhotoCount = COUNT(*)
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId;

    IF @PhotoCount >= @MaxPhotos
    BEGIN
        THROW 51345, 'This ticket already has the maximum number of photos.', 1;
    END

    INSERT INTO [Support].[TicketPhotos] ([TicketId], [PhotoUrl], [CreatedAtUtc])
    VALUES (@TicketId, @PhotoUrl, @Now);

    -- Adding evidence is activity on the ticket, so it moves the timestamp the
    -- "my tickets" list sorts on by default.
    UPDATE [Support].[Tickets]
    SET [UpdatedAtUtc] = @Now
    WHERE [TicketId] = @TicketId;

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;

    COMMIT TRANSACTION;
END;
