-- One prescription per booking, recorded by the vet when a Vet job is
-- started/completed (Booking.Bookings.ServiceCategory = 'Vet'). Distinct from the
-- pet's standing medical record (Parent.Pets) — this is a per-visit snapshot the
-- parent app renders on its "View Prescription" screen. Upserted: re-recording
-- replaces the row. The next-consultation date is NOT stored here — it lives on
-- Parent.PetNextConsultations (the pet's rolling per-type follow-up) and is joined
-- into the booking-detail read.
CREATE TABLE [Booking].[BookingPrescriptions]
(
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    -- Free-text prescription / notes the vet recorded. Optional.
    [PrescriptionText] NVARCHAR(4000) NULL,
    -- Whether the vet marked the pet as vaccinated during this visit.
    [IsPetVaccinated] BIT NOT NULL,
    -- The vaccines administered/recorded, as a JSON array of names
    -- (e.g. ["Rabies","DHPP"]). NULL when none. Serialized/parsed by the app
    -- (System.Text.Json) — the whole list is read/written per booking.
    [Vaccinations] NVARCHAR(MAX) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingPrescriptions_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingPrescriptions_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingPrescriptions] PRIMARY KEY CLUSTERED ([BookingId] ASC),
    CONSTRAINT [FK_BookingPrescriptions_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE
);
