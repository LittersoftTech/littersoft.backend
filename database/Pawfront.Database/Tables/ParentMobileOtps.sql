CREATE TABLE [Parent].[ParentMobileOtps]
(
    [ParentMobileOtpId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [MobileCountryCode] NVARCHAR(8) NOT NULL,
    [MobileNumber] NVARCHAR(32) NOT NULL,
    [OtpCodeHash] VARBINARY(32) NOT NULL,
    [OtpCodeLastTwo] NVARCHAR(2) NOT NULL,
    [ValidationStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_ParentMobileOtps_ValidationStatus] DEFAULT N'Pending',
    [FailedAttemptCount] INT NOT NULL
        CONSTRAINT [DF_ParentMobileOtps_FailedAttemptCount] DEFAULT 0,
    [DateSentUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ParentMobileOtps_DateSentUtc] DEFAULT SYSUTCDATETIME(),
    [DateValidatedUtc] DATETIME2(7) NULL,
    [ExpiresAtUtc] DATETIME2(7) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ParentMobileOtps_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ParentMobileOtps_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ParentMobileOtps] PRIMARY KEY CLUSTERED ([ParentMobileOtpId] ASC),
    CONSTRAINT [FK_ParentMobileOtps_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
    CONSTRAINT [CK_ParentMobileOtps_ValidationStatus] CHECK ([ValidationStatus] IN (N'Pending', N'Validated', N'Expired'))
);

GO

CREATE INDEX [IX_ParentMobileOtps_PetParentId_DateSentUtc]
    ON [Parent].[ParentMobileOtps] ([PetParentId], [DateSentUtc] DESC);
