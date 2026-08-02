CREATE OR ALTER PROCEDURE [Booking].[UpsertBookingPrescription]
    @BookingId        UNIQUEIDENTIFIER,
    @ProviderId       UNIQUEIDENTIFIER,
    @PrescriptionText NVARCHAR(4000),
    @IsPetVaccinated  BIT,
    @Vaccinations     NVARCHAR(MAX)      -- JSON array of vaccine names, or NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Records (or replaces) the vet's prescription for a booking. Only the
    -- provider on the booking may write it, only for a Vet service, and only once
    -- the job is underway (IN_PROGRESS; the retired ENDING kept for legacy
    -- rows) or finished (COMPLETED) — you don't
    -- prescribe before seeing the pet. The next-consultation date is not stored
    -- here; it rides on Parent.PetNextConsultations and is joined on read.
    DECLARE @RowProviderId     UNIQUEIDENTIFIER;
    DECLARE @RowServiceCategory NVARCHAR(64);
    DECLARE @RowStatus         NVARCHAR(48);

    SELECT @RowProviderId      = [ProviderId],
           @RowServiceCategory = [ServiceCategory],
           @RowStatus          = [Status]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    IF @RowProviderId IS NULL
        THROW 51290, 'Booking not found.', 1;

    IF @RowProviderId <> @ProviderId
        THROW 51291, 'You are not the provider on this booking.', 1;

    IF @RowServiceCategory <> N'Vet'
        THROW 51292, 'A prescription can only be recorded on a Vet booking.', 1;

    IF @RowStatus NOT IN (N'IN_PROGRESS', N'ENDING', N'COMPLETED')
        THROW 51293, 'A prescription can only be recorded once the job has started or completed.', 1;

    UPDATE [Booking].[BookingPrescriptions]
    SET [PrescriptionText] = @PrescriptionText,
        [IsPetVaccinated]  = @IsPetVaccinated,
        [Vaccinations]     = @Vaccinations,
        [UpdatedAtUtc]     = SYSUTCDATETIME()
    WHERE [BookingId] = @BookingId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [Booking].[BookingPrescriptions]
            ([BookingId], [PrescriptionText], [IsPetVaccinated], [Vaccinations])
        VALUES
            (@BookingId, @PrescriptionText, @IsPetVaccinated, @Vaccinations);
    END

    SELECT bp.[BookingId],
           bp.[PrescriptionText],
           bp.[IsPetVaccinated],
           bp.[Vaccinations],
           nc.[NextConsultationDate],
           bp.[CreatedAtUtc],
           bp.[UpdatedAtUtc]
    FROM [Booking].[BookingPrescriptions] AS bp
    INNER JOIN [Booking].[Bookings] AS b
        ON b.[BookingId] = bp.[BookingId]
    LEFT JOIN [Parent].[PetNextConsultations] AS nc
        ON nc.[PetId] = b.[PetId] AND nc.[ConsultationType] = N'Vet'
    WHERE bp.[BookingId] = @BookingId;
END;
