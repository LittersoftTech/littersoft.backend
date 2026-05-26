CREATE OR ALTER PROCEDURE [Provider].[DeactivateProviderService]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceType NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Soft-deactivate (don't delete) so historical closures/bookings referencing
    -- this ServiceId remain valid. The row can be re-activated on next offering
    -- save via UpsertProviderService.
    UPDATE [Provider].[ProviderServices]
    SET [IsActive] = 0,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId
      AND [ServiceType] = @ServiceType
      AND [IsActive] = 1;
END;
