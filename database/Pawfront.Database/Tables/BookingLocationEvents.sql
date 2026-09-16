-- Append-only record of WHERE each party was at the moments of a single-day
-- booking that matter in a dispute: arrival, job start, no-show, the parent
-- showing their start code, cash changing hands (or not), and evidence capture.
--
-- Deliberately NOT columns on [Booking].[BookingStatusHistory], for two reasons:
-- an audit row has exactly ONE [ChangedByActor] while several triggers here want
-- BOTH parties' locations, and half the triggers (ArrivalConfirmed, StartOtpShown,
-- CashNotReceived, EvidenceCaptured) are not status changes at all, so there is no
-- audit row to hang them off. Correlate the two on (BookingId, RecordedAtUtc);
-- [Trigger] already names the moment unambiguously, so no FK between them is kept
-- (which also avoids a second cascade path from [Booking].[Bookings]).
--
-- One row per (booking, trigger, capturing party). Append-only and NOT unique on
-- that triple: a client retrying after a dropped response must not be refused, and
-- EvidenceCaptured legitimately fires once per photo.
--
-- Coordinates are NOT NULL by design. The API rejects any of these actions that
-- arrives without a usable fix (400 InvalidRequest), so a booking either has the
-- location for a moment or the moment never happened — there is no third state
-- recording that somebody declined to share.
CREATE TABLE [Booking].[BookingLocationEvents]
(
    [BookingLocationEventId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingLocationEvents_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    -- Which moment this fix belongs to. ArrivalConfirmed covers BOTH arrival
    -- questions — "have you arrived at the customer's location?" and "has the
    -- customer arrived?" — because which one was asked follows entirely from the
    -- booking's own [LocationType], and deriving it there means a client cannot
    -- misreport which question it put on screen.
    [Trigger] NVARCHAR(32) NOT NULL,
    -- The party whose device produced this fix: 'Provider' or 'Parent'. Each app
    -- reports its OWN position; the server never copies one party's coordinate
    -- onto the other's action, so a missing row honestly reads as "we do not know
    -- where they were" rather than as a stale point presented as a live one.
    [CapturedByType] NVARCHAR(16) NOT NULL,
    [CapturedById] UNIQUEIDENTIFIER NOT NULL,
    [Latitude] DECIMAL(9, 6) NOT NULL,
    [Longitude] DECIMAL(9, 6) NOT NULL,
    -- The device's own reported horizontal accuracy, in metres. Optional, and
    -- worth keeping: a fix good to 5 m and one good to 3 km support very
    -- different claims about whether somebody was actually at an address.
    [AccuracyMetres] DECIMAL(9, 2) NULL,
    -- When the DEVICE took the reading, as reported by the client. Optional and
    -- untrusted — it is the client's clock. [RecordedAtUtc] is the server's own
    -- stamp and is the one to reason about; a wide gap between the two is itself
    -- a signal that a cached fix was replayed.
    [DeviceCapturedAtUtc] DATETIME2(7) NULL,
    [RecordedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingLocationEvents_RecordedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingLocationEvents] PRIMARY KEY CLUSTERED ([BookingLocationEventId] ASC),
    CONSTRAINT [FK_BookingLocationEvents_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_BookingLocationEvents_Trigger]
        CHECK ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded', N'NoShowMarked',
                            N'StartOtpShown', N'CashReceived', N'CashNotReceived',
                            N'EvidenceCaptured')),
    CONSTRAINT [CK_BookingLocationEvents_CapturedByType]
        CHECK ([CapturedByType] IN (N'Provider', N'Parent')),
    -- Belt-and-braces on top of the app layer: the two pre-start triggers are the
    -- provider answering a question on their own screen, and StartOtpShown is the
    -- parent opening their code. Neither can come from the other side.
    CONSTRAINT [CK_BookingLocationEvents_TriggerParty]
        CHECK (NOT ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded') AND [CapturedByType] = N'Parent')
           AND NOT ([Trigger] = N'StartOtpShown' AND [CapturedByType] = N'Provider')),
    CONSTRAINT [CK_BookingLocationEvents_Latitude]
        CHECK ([Latitude] BETWEEN -90 AND 90),
    CONSTRAINT [CK_BookingLocationEvents_Longitude]
        CHECK ([Longitude] BETWEEN -180 AND 180),
    CONSTRAINT [CK_BookingLocationEvents_AccuracyMetres]
        CHECK ([AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0)
);

GO

-- Serves the per-booking read-back (the admin/support timeline). Covering, so
-- reconstructing a booking's whole location history is one seek.
CREATE INDEX [IX_BookingLocationEvents_Booking_RecordedAt]
    ON [Booking].[BookingLocationEvents] ([BookingId], [RecordedAtUtc] ASC)
    INCLUDE ([Trigger], [CapturedByType], [CapturedById], [Latitude], [Longitude],
             [AccuracyMetres], [DeviceCapturedAtUtc]);
