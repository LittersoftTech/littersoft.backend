CREATE TABLE [Provider].[ProviderTrust]
(
    ProviderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ProviderTrust PRIMARY KEY,
    RevisionId UNIQUEIDENTIFIER NOT NULL,
    ServiceCategory NVARCHAR(64) NOT NULL,
    EvidenceKind NVARCHAR(16) NOT NULL,
    Status NVARCHAR(16) NOT NULL,
    DocumentUrl NVARCHAR(1000) NULL,
    RejectionReason NVARCHAR(2000) NULL,
    MedRegReference NVARCHAR(1000) NULL,
    UpdatedAtUtc DATETIME2(7) NOT NULL,
    CONSTRAINT FK_ProviderTrust_Providers FOREIGN KEY (ProviderId) REFERENCES Provider.Providers(ProviderId) ON DELETE CASCADE,
    CONSTRAINT CK_ProviderTrust_Status CHECK (Status IN (N'InReview',N'Added',N'NotApproved')),
    CONSTRAINT CK_ProviderTrust_Evidence CHECK (
        (EvidenceKind=N'Document' AND ServiceCategory IN (N'PetSitter',N'PetGroomer',N'PetTrainer',N'Vet') AND DocumentUrl IS NOT NULL AND MedRegReference IS NULL)
        OR (EvidenceKind=N'MedReg' AND ServiceCategory=N'Vet' AND DocumentUrl IS NOT NULL AND MedRegReference IS NOT NULL)),
    CONSTRAINT CK_ProviderTrust_Reason CHECK (Status<>N'NotApproved' OR NULLIF(LTRIM(RTRIM(RejectionReason)),N'') IS NOT NULL)
);
