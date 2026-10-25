-- Atomic single-column increment for the three event-level counters.
-- Called by the public increment endpoints (any signed-in user). Throws
-- 51096 if the event id is unknown so the mobile client gets a clear
-- signal rather than silently incrementing nothing.
CREATE OR ALTER PROCEDURE [Event].[IncrementEventCounter]
    @EventId UNIQUEIDENTIFIER,
    @CounterType NVARCHAR(16),
    @ViewerAccountId UNIQUEIDENTIFIER=NULL, @ViewerType NVARCHAR(16)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @CounterType NOT IN (N'View', N'Share', N'Inquiry')
    BEGIN
        THROW 51097, 'CounterType must be View, Share, or Inquiry.', 1;
    END

    DECLARE @RowsAffected INT;

    IF @CounterType = N'View'
    BEGIN
        IF @ViewerAccountId IS NULL OR @ViewerType IS NULL OR @ViewerType NOT IN (N'PetParent',N'Provider')
            THROW 51097, 'An authenticated account profile is required to record a view.', 1;
        BEGIN TRANSACTION;
        -- Lock the event first so concurrent first views insert and increment once.
        DECLARE @HostParentId UNIQUEIDENTIFIER,@HostProviderId UNIQUEIDENTIFIER;
        SELECT @HostParentId=PetParentId,@HostProviderId=ProviderId
        FROM [Event].[Events] WITH (UPDLOCK,HOLDLOCK) WHERE EventId=@EventId;
        SET @RowsAffected=@@ROWCOUNT;
        IF @RowsAffected=1
           AND NOT ((@ViewerType=N'PetParent' AND ISNULL(@HostParentId,'00000000-0000-0000-0000-000000000000')=@ViewerAccountId)
                 OR (@ViewerType=N'Provider' AND ISNULL(@HostProviderId,'00000000-0000-0000-0000-000000000000')=@ViewerAccountId))
           AND NOT EXISTS (SELECT 1 FROM [Event].[EventViews]
                           WHERE EventId=@EventId AND ViewerType=@ViewerType AND ViewerAccountId=@ViewerAccountId)
        BEGIN
            INSERT [Event].[EventViews](EventId,ViewerType,ViewerAccountId) VALUES (@EventId,@ViewerType,@ViewerAccountId);
            UPDATE [Event].[Events] SET ViewCount=ViewCount+1 WHERE EventId=@EventId;
        END
        COMMIT TRANSACTION;
    END
    ELSE IF @CounterType = N'Share'
    BEGIN
        UPDATE [Event].[Events]
        SET [ShareCount] = [ShareCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END
    ELSE
    BEGIN
        UPDATE [Event].[Events]
        SET [InquiryCount] = [InquiryCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END

    IF @RowsAffected = 0
    BEGIN
        THROW 51096, 'Event was not found.', 1;
    END

    -- Return the new value so the client can render the updated count
    -- without a follow-up read.
    SELECT [ViewCount], [ShareCount], [InquiryCount]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;
END;
