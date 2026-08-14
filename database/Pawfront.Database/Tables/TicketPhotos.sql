-- Photos attached to a booking incident. One row per uploaded photo — the same
-- shape as [Review].[BookingReviewPhotos] and [Booking].[BookingEvidence].
--
-- The blob upload happens in the app layer under the [IncidentPhotos] folder
-- ("incident-photos/<ticketId>/<guid>.<ext>"), which is why photos are a SECOND
-- call after the ticket row exists: the blob owner id is the ticket's own id. The
-- row here is the source of truth.
--
-- These live in SQL rather than in the Cosmos ticket document, even though the
-- rest of the narrative is over there. The reason is the 5-photo cap: enforcing
-- it needs a count taken under a lock ([Support].[AddTicketPhoto] holds
-- UPDLOCK + HOLDLOCK), and two uploads in flight against a Cosmos document would
-- each read "room for one more". The document holds the words; this holds the
-- countable thing.
--
-- Only booking incidents carry photos. A chat incident needs none — the images
-- already in the thread are the evidence, and the whole conversation is under
-- legal hold — so [Support].[AddTicketPhoto] rejects that type.
--
-- Deleting a photo is deliberately NOT supported: evidence a reporter can retract
-- after support has read it would defeat the point of the hold. This is the one
-- gallery in the product with no delete path.
CREATE TABLE [Support].[TicketPhotos]
(
    [TicketPhotoId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_TicketPhotos_Id] DEFAULT NEWSEQUENTIALID(),
    [TicketId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_TicketPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_TicketPhotos] PRIMARY KEY CLUSTERED ([TicketPhotoId] ASC),
    CONSTRAINT [FK_TicketPhotos_Tickets_TicketId]
        FOREIGN KEY ([TicketId]) REFERENCES [Support].[Tickets] ([TicketId])
        ON DELETE CASCADE
);

GO

-- Photos are always read for a known ticket (or a page of them), oldest-first.
CREATE INDEX [IX_TicketPhotos_Ticket_Created]
    ON [Support].[TicketPhotos] ([TicketId], [CreatedAtUtc] ASC)
    INCLUDE ([PhotoUrl]);
