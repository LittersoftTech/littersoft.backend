-- Raises a support ticket against the counterparty.
--
-- Reporting somebody does NOT block them. Nothing here writes to
-- [Block].[BlockedParticipants]: the pair stay able to message, book and find each
-- other while support looks at the case, and blocking remains what it always was
-- — the users' own remedy, placed by hand through the chat host and lifted the
-- same way. A ticket is a report to support, not a sanction applied on the
-- reporter's say-so.
--
-- Handles ALL FOUR kinds, because the only thing that differs between them is
-- where the party ids come from:
--   @TicketType = 'BookingIncident' -> @BookingType + @BookingId name the job.
--                                      Both party ids and the pet come off the
--                                      booking row.
--   @TicketType = 'ChatIncident'    -> @ConversationId names the thread. Both
--                                      party ids come off the conversation row.
--   @TicketType = 'EventIncident'   -> @EventId names the event. There is NO
--                                      counterparty: only the reporter's own
--                                      party column is set (see below).
--   @TicketType = 'AppIssue'        -> no subject at all. Reporter only.
--
-- The reporter passes only their OWN id (@ActorId) and which side they are on.
-- For the two counterparty kinds the OTHER party is DERIVED from the subject and
-- never accepted from the caller, which is what stops a report being filed
-- against somebody who was never party to the booking or thread.
--
-- An event report records no counterparty on purpose. The organiser is one join
-- away through @EventId, and a parent-organised event reported by another parent
-- could not be stored in the one-Provider / one-PetParent shape anyway. An event
-- is public: anyone signed in can see one, so anyone signed in can report one —
-- there is no attendance check.
--
-- ONE open ticket per SUBJECT, not per person:
--   booking      -> per (BookingType, BookingId), either direction
--   conversation -> per ConversationId, either direction
--   event        -> per (EventId, REPORTER). An event has many attendees and each
--                   of them reporting it is a separate account; what is refused
--                   is the same person reporting it twice.
--   app issue    -> no rule. Each bug report is a different bug.
-- The check and the insert share a transaction so the read cannot go stale
-- between them, with [UX_Tickets_OpenBooking] / [UX_Tickets_OpenConversation] /
-- [UX_Tickets_OpenEventReporter] as the race-safe backstop underneath.
--
-- Returns TWO result sets:
--   1. [Outcome] = 'Created' | 'TicketAlreadyOpen', followed by the ticket row.
--      On 'TicketAlreadyOpen' the row is the ticket ALREADY open on this booking
--      or conversation — nothing was written — so the caller can answer 409
--      naming it rather than leaving the reporter at a dead end. Discriminated
--      rather than THROWn for the same reason
--      [Provider].[SetProviderActiveStatus] and [Provider].[CreateClosures] are:
--      the conflict carries data the app needs.
--   2. the ticket's photos — always empty here (photos are a second call, keyed
--      by the ticket id this mints), present so the read paths share one shape.
--
-- THROWs: 51340 booking not found, 51341 caller is not a party to the subject,
-- 51342 conversation not found, 51343 invalid request (defensive), 51348 Custom
-- walk-in (no pet parent to report or be reported), 51355 event not found.
CREATE OR ALTER PROCEDURE [Support].[CreateTicket]
    @TicketType NVARCHAR(24),
    @RaisedByType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @BookingType NVARCHAR(16) = NULL,
    @BookingId UNIQUEIDENTIFIER = NULL,
    @ConversationId UNIQUEIDENTIFIER = NULL,
    @Category NVARCHAR(100) = NULL,
    @Reason NVARCHAR(500) = NULL,
    @EventId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive: the API validates all of this first, so reaching these is a
    -- direct-caller error rather than something a client can provoke.
    IF @TicketType NOT IN (N'BookingIncident', N'ChatIncident', N'EventIncident', N'AppIssue')
        OR @RaisedByType NOT IN (N'Provider', N'PetParent')
        OR @ActorId IS NULL
        OR (@TicketType = N'BookingIncident'
            AND (@BookingId IS NULL OR @BookingType NOT IN (N'SingleDay', N'NightStay')))
        OR (@TicketType = N'ChatIncident' AND @ConversationId IS NULL)
        OR (@TicketType = N'EventIncident' AND @EventId IS NULL)
    BEGIN
        THROW 51343, 'Invalid support ticket request.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @PetId UNIQUEIDENTIFIER;
    DECLARE @Found BIT = 0;
    DECLARE @TicketId UNIQUEIDENTIFIER;
    DECLARE @ExistingTicketId UNIQUEIDENTIFIER;
    DECLARE @Outcome NVARCHAR(24);

    -- Blank and absent are the same thing for both classifiers, so a client sending
    -- an empty picker value stores NULL rather than an empty string that every read
    -- would then have to treat as "not set".
    IF LTRIM(RTRIM(COALESCE(@Category, N''))) = N''
    BEGIN
        SET @Category = NULL;
    END

    IF LTRIM(RTRIM(COALESCE(@Reason, N''))) = N''
    BEGIN
        SET @Reason = NULL;
    END

    BEGIN TRANSACTION;

    IF @TicketType = N'BookingIncident'
    BEGIN
        -- No UPDLOCK on the booking: nothing about its status affects whether it
        -- can be reported, so this read cannot go stale in a way that matters.
        -- Any booking either party is on is reportable, at any point in its life —
        -- an incident is a statement about what happened, not a transition.
        IF @BookingType = N'SingleDay'
        BEGIN
            SELECT @ProviderId = [ProviderId],
                   @PetParentId = [PetParentId],
                   @PetId = [PetId],
                   @Found = 1
            FROM [Booking].[Bookings]
            WHERE [BookingId] = @BookingId;
        END
        ELSE
        BEGIN
            SELECT @ProviderId = [ProviderId],
                   @PetParentId = [PetParentId],
                   @PetId = [PetId],
                   @Found = 1
            FROM [Booking].[NightStayBookings]
            WHERE [NightStayBookingId] = @BookingId;
        END

        IF @Found = 0
        BEGIN
            THROW 51340, 'Booking was not found.', 1;
        END

        -- A Custom walk-in carries free-text customer details and no PetParentId,
        -- so there is no second party to report or to be reported.
        -- (Night-stay is App-only, so this can only fire on a single-day booking.)
        IF @PetParentId IS NULL
        BEGIN
            THROW 51348, 'Only app bookings can be reported.', 1;
        END
    END
    ELSE IF @TicketType = N'ChatIncident'
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @Found = 1
        FROM [Chat].[Conversations]
        WHERE [ConversationId] = @ConversationId;

        IF @Found = 0
        BEGIN
            THROW 51342, 'Conversation was not found.', 1;
        END
    END
    ELSE
    BEGIN
        -- 'EventIncident' and 'AppIssue': the reporter is the only party, so
        -- their own column is set from @RaisedByType and the other stays NULL.
        -- CK_Tickets_SubjectMatchesType enforces exactly that shape.
        IF @RaisedByType = N'Provider'
        BEGIN
            SET @ProviderId = @ActorId;
        END
        ELSE
        BEGIN
            SET @PetParentId = @ActorId;
        END

        IF @TicketType = N'EventIncident'
        BEGIN
            -- Existence only. An event is public, so there is no attendance or
            -- ownership test to apply — anyone who can see one can report it.
            IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId)
            BEGIN
                THROW 51355, 'Event was not found.', 1;
            END
        END
    END

    -- The caller must be the side they claim to be, on the subject they named.
    -- Trivially satisfied for the two reporter-only kinds, where the column was
    -- just set FROM @ActorId — deliberately left to fall through rather than
    -- branched around, so there is one place this rule is stated.
    IF (@RaisedByType = N'Provider' AND @ProviderId <> @ActorId)
        OR (@RaisedByType = N'PetParent' AND @PetParentId <> @ActorId)
    BEGIN
        THROW 51341, 'You are not a party to this.', 1;
    END

    -- Scoped to the SUBJECT, not to the pair: this is what lets a parent report
    -- every booking they have with one provider, while still refusing a second
    -- report of the SAME job.
    --
    -- UPDLOCK + HOLDLOCK over the matching filtered-unique range. With no open
    -- ticket yet this takes a range lock, so two reports filed at the same
    -- instant serialise: the second finds the first rather than both inserting
    -- and one failing on the unique index. Either party's open ticket is found,
    -- since one incident is one case however many people report it.
    IF @TicketType = N'BookingIncident'
    BEGIN
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [BookingId] = @BookingId
          AND [BookingType] = @BookingType
          AND [Status] <> N'CLOSED';
    END
    ELSE IF @TicketType = N'ChatIncident'
    BEGIN
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ConversationId] = @ConversationId
          AND [Status] <> N'CLOSED';
    END
    ELSE IF @TicketType = N'EventIncident'
    BEGIN
        -- Scoped to the REPORTER as well as the event, unlike the two above: a
        -- second attendee reporting the same event is a second account of it and
        -- gets its own ticket. Matches [UX_Tickets_OpenEventReporter], which is
        -- the backstop if two of this reporter's requests land together.
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [EventId] = @EventId
          AND [Status] <> N'CLOSED'
          AND ((@RaisedByType = N'Provider'  AND [ProviderId]  = @ActorId)
            OR (@RaisedByType = N'PetParent' AND [PetParentId] = @ActorId));
    END
    -- 'AppIssue' has no subject, so nothing to collide with: every report is a
    -- new ticket. @ExistingTicketId stays NULL and the insert below always runs.

    IF @ExistingTicketId IS NOT NULL
    BEGIN
        -- One open ticket per booking / per conversation (either direction), or
        -- per event per reporter.
        -- Nothing is written; the caller reports the conflict naming this ticket.
        SET @Outcome = N'TicketAlreadyOpen';
        SET @TicketId = @ExistingTicketId;
    END
    ELSE
    BEGIN
        SET @Outcome = N'Created';

        DECLARE @Inserted TABLE ([TicketId] UNIQUEIDENTIFIER);

        -- The ticket row is the ONLY thing written. Nothing touches
        -- [Block].[BlockedParticipants]: a report is not a block, and the reported
        -- party keeps every ability they had — messaging, booking, discovery —
        -- until support decides otherwise off-platform.
        INSERT INTO [Support].[Tickets]
            ([TicketType], [ProviderId], [PetParentId], [RaisedByType],
             [BookingType], [BookingId], [PetId], [ConversationId], [EventId],
             [Category], [Reason], [Status], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[TicketId] INTO @Inserted
        VALUES
            (@TicketType, @ProviderId, @PetParentId, @RaisedByType,
             @BookingType, @BookingId, @PetId, @ConversationId, @EventId,
             @Category, @Reason, N'OPENED', @Now, @Now);

        SELECT @TicketId = [TicketId] FROM @Inserted;
    END

    SELECT @Outcome AS [Outcome],
           t.[TicketId],
           t.[TicketNumber],
           t.[TicketType],
           t.[ProviderId],
           t.[PetParentId],
           t.[RaisedByType],
           t.[BookingType],
           t.[BookingId],
           t.[PetId],
           t.[ConversationId],
           t.[Status],
           t.[CreatedAtUtc],
           t.[UpdatedAtUtc],
           t.[ClosedAtUtc],
           -- The reporter's two classifiers and the event subject, appended LAST
           -- here and in the five other procedures that project this row, so
           -- the existing reader ordinals did not shift when they were added.
           t.[Category],
           t.[Reason],
           t.[EventId]
    FROM [Support].[Tickets] AS t
    WHERE t.[TicketId] = @TicketId;

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;

    COMMIT TRANSACTION;
END;
