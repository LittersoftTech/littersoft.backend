CREATE TABLE [Parent].[PetNextConsultations]
(
    [PetNextConsultationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_PetNextConsultations_PetNextConsultationId] DEFAULT NEWSEQUENTIALID(),
    [PetId] UNIQUEIDENTIFIER NOT NULL,
    -- Which kind of provider proposed the follow-up. One row per (pet, type):
    -- a newer date from the same provider type replaces the old one (upsert).
    [ConsultationType] NVARCHAR(16) NOT NULL,
    [NextConsultationDate] DATE NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetNextConsultations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetNextConsultations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_PetNextConsultations] PRIMARY KEY CLUSTERED ([PetNextConsultationId] ASC),
    CONSTRAINT [FK_PetNextConsultations_Pets_PetId]
        FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]) ON DELETE CASCADE,
    CONSTRAINT [CK_PetNextConsultations_ConsultationType]
        CHECK ([ConsultationType] IN (N'Groomer', N'Vet', N'Trainer')),
    CONSTRAINT [UQ_PetNextConsultations_PetId_ConsultationType]
        UNIQUE ([PetId], [ConsultationType])
);
