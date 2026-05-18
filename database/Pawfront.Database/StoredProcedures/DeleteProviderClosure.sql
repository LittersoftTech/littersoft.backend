CREATE OR ALTER PROCEDURE [Provider].[DeleteClosure]
    @ProviderId UNIQUEIDENTIFIER,
    @ClosureId  UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [Provider].[ProviderClosures]
    WHERE [ClosureId] = @ClosureId
      AND [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51071, 'Provider closure was not found for this provider.', 1;
    END
END;
