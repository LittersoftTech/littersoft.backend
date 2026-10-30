-- One view per authenticated account, including provider-side viewers.
CREATE TABLE [Event].[EventViews]
(
    EventId UNIQUEIDENTIFIER NOT NULL REFERENCES [Event].[Events](EventId),
    ViewerAccountId UNIQUEIDENTIFIER NOT NULL,
    ViewerType NVARCHAR(16) NOT NULL CHECK (ViewerType IN (N'PetParent',N'Provider')),
    ViewedAtUtc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_EventViews PRIMARY KEY (EventId,ViewerType,ViewerAccountId)
);
