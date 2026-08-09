-- Opens the one thread between a provider and a pet parent, creating it if this
-- is the first contact.
--
-- Chat is OPEN — any parent may message any provider with no booking between them
-- — so this procedure is where "may these two talk at all" is decided, and the
-- only gates are: both accounts still exist, and neither has blocked the other.
--
-- An INACTIVE provider is deliberately still reachable. Inactive means "not
-- taking bookings": they are hidden from discovery and [Booking].[CreateBooking]
-- refuses them, but their profile is still viewable by deep link and answering a
-- question before switching back on is exactly what chat is for. Only IsDeleted —
-- which is permanent — closes the door.
--
-- Race safety comes from the UNIQUE ([ProviderId], [PetParentId]) on
-- [Chat].[Conversations]: the lookup below takes UPDLOCK + HOLDLOCK over that key
-- range, so two devices opening the same thread at once serialise and the second
-- finds the first's row instead of hitting a UNIQUE violation. Same shape as
-- [Review].[UpsertBookingReview].
--
-- Returns TWO result sets: the conversation row, then both participant rows.
--
-- THROWs: 51320 provider not found, 51321 provider account deleted,
-- 51322 pet parent not found or deleted, 51323 blocked in one direction or the
-- other, 51324 the actor is not one of the two named parties.
CREATE OR ALTER PROCEDURE [Chat].[GetOrCreateConversation]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),        -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive. The API derives the actor from the JWT and never from the body,
    -- so a mismatch here means a direct caller, not something a client can reach.
    IF @ActorType NOT IN (N'Provider', N'PetParent')
        OR (@ActorType = N'Provider' AND @ActorId <> @ProviderId)
        OR (@ActorType = N'PetParent' AND @ActorId <> @PetParentId)
    BEGIN
        THROW 51324, 'The acting participant is not a party to this conversation.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ConversationId UNIQUEIDENTIFIER;
    DECLARE @ProviderIsDeleted BIT;
    DECLARE @ParentIsDeleted BIT;

    BEGIN TRANSACTION;

    SELECT @ProviderIsDeleted = [IsDeleted]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsDeleted IS NULL
    BEGIN
        THROW 51320, 'Provider was not found.', 1;
    END

    IF @ProviderIsDeleted = 1
    BEGIN
        THROW 51321, 'This provider account has been deleted.', 1;
    END

    SELECT @ParentIsDeleted = [IsDeleted]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    -- Deleted and absent are one case on this side. A deleted parent has had
    -- their Firebase identity severed, so they could never read the thread; the
    -- provider gains nothing from being told the difference.
    IF @ParentIsDeleted IS NULL OR @ParentIsDeleted = 1
    BEGIN
        THROW 51322, 'Pet parent was not found.', 1;
    END

    -- Either direction blocks. The blocked party is not told which way round it
    -- is — that would confirm the other person acted, which is the thing a block
    -- is meant to end.
    IF EXISTS (
        SELECT 1
        FROM [Chat].[BlockedParticipants]
        WHERE ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
               AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId)
           OR ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
               AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId))
    BEGIN
        THROW 51323, 'This conversation is not available.', 1;
    END

    -- UPDLOCK + HOLDLOCK over the unique pair: when no row exists this takes a
    -- range lock, so a concurrent open of the same thread waits here rather than
    -- racing to INSERT.
    SELECT @ConversationId = [ConversationId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId
      AND [PetParentId] = @PetParentId;

    IF @ConversationId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([ConversationId] UNIQUEIDENTIFIER);

        INSERT INTO [Chat].[Conversations]
            ([ProviderId], [PetParentId], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[ConversationId] INTO @Inserted
        VALUES
            (@ProviderId, @PetParentId, @Now, @Now);

        SELECT @ConversationId = [ConversationId] FROM @Inserted;

        -- Both sides, always, in the same statement — a conversation with one
        -- participant row is not a state any read path knows how to handle.
        INSERT INTO [Chat].[ConversationParticipants]
            ([ConversationId], [ParticipantType], [ParticipantId], [CreatedAtUtc], [UpdatedAtUtc])
        VALUES
            (@ConversationId, N'Provider', @ProviderId, @Now, @Now),
            (@ConversationId, N'PetParent', @PetParentId, @Now, @Now);
    END

    SELECT [ConversationId],
           [ProviderId],
           [PetParentId],
           [LastSequence],
           [LastMessageAtUtc],
           [LastMessagePreview],
           [LastMessageSenderType],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Chat].[Conversations]
    WHERE [ConversationId] = @ConversationId;

    SELECT [ConversationParticipantId],
           [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
    ORDER BY [ParticipantType] ASC;

    COMMIT TRANSACTION;
END;
