CREATE OR ALTER PROCEDURE [Provider].[ListProviderServices]
    @ProviderId UNIQUEIDENTIFIER,
    @IncludeInactive BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ServiceId], [ProviderId], [ServiceCategory], [SubCategory],
           [ServiceType], [IsActive], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServices]
    WHERE [ProviderId] = @ProviderId
      AND (@IncludeInactive = 1 OR [IsActive] = 1)
    ORDER BY [ServiceType];
END;
