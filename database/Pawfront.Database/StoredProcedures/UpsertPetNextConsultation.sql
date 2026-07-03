-- Records (or replaces) a pet's next-consultation date for one provider type
-- (Groomer | Vet | Trainer). Called by the provider's booking-complete flow —
-- one row per (PetId, ConsultationType); a newer date replaces the old one.
-- THROW 51221 when the pet row is missing.
CREATE OR ALTER PROCEDURE [Parent].[UpsertPetNextConsultation]
    @PetId UNIQUEIDENTIFIER,
    @ConsultationType NVARCHAR(16),
    @NextConsultationDate DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [PetId] = @PetId)
    BEGIN
        THROW 51221, 'Pet was not found.', 1;
    END

    UPDATE [Parent].[PetNextConsultations]
    SET [NextConsultationDate] = @NextConsultationDate,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId
      AND [ConsultationType] = @ConsultationType;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [Parent].[PetNextConsultations]
            ([PetId], [ConsultationType], [NextConsultationDate])
        VALUES
            (@PetId, @ConsultationType, @NextConsultationDate);
    END

    SELECT [PetNextConsultationId],
           [PetId],
           [ConsultationType],
           [NextConsultationDate],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetNextConsultations]
    WHERE [PetId] = @PetId
      AND [ConsultationType] = @ConsultationType;

    COMMIT TRANSACTION;
END;
