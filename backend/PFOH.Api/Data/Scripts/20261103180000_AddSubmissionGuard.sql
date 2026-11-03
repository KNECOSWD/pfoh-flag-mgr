-- Submission guard, release 5.3.0.
-- Additive and idempotent. Safe to run more than once.
-- Does not update or delete existing rows.
-- The owner applies this to production. The app does not run it against production by itself
-- beyond the same statements in SubmissionGuardSchema.EnsureAsync at startup.

IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SubmissionReceipts] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SubmissionReceipts] PRIMARY KEY,
        [ChangeRequestId] int NOT NULL,
        [PublicToken] uniqueidentifier NOT NULL,
        [PublicCode] nvarchar(16) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        CONSTRAINT [FK_SubmissionReceipts_HonoreeChangeRequests_ChangeRequestId]
            FOREIGN KEY ([ChangeRequestId]) REFERENCES [dbo].[HonoreeChangeRequests] ([Id])
    );
    CREATE UNIQUE INDEX [UX_SubmissionReceipts_ChangeRequestId]
        ON [dbo].[SubmissionReceipts] ([ChangeRequestId]);
    CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicToken]
        ON [dbo].[SubmissionReceipts] ([PublicToken]);
    CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicCode]
        ON [dbo].[SubmissionReceipts] ([PublicCode]);
END
IF OBJECT_ID(N'[dbo].[SubmissionAuditEvents]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SubmissionAuditEvents] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SubmissionAuditEvents] PRIMARY KEY,
        [Action] nvarchar(32) NOT NULL,
        [ChangeRequestId] int NOT NULL,
        [KeeperChangeRequestId] int NULL,
        [ActorEmail] nvarchar(256) NOT NULL,
        [ActorObjectId] nvarchar(128) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [Note] nvarchar(500) NULL
    );
    CREATE INDEX [IX_SubmissionAuditEvents_ChangeRequestId]
        ON [dbo].[SubmissionAuditEvents] ([ChangeRequestId], [CreatedUtc]);
END
IF COL_LENGTH(N'dbo.HonoreeChangeRequests', N'CorrectsChangeRequestId') IS NULL
    ALTER TABLE [dbo].[HonoreeChangeRequests] ADD [CorrectsChangeRequestId] int NULL;
IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.SubmissionReceipts', N'PublicCode') IS NULL
    ALTER TABLE [dbo].[SubmissionReceipts] ADD [PublicCode] nvarchar(16) NULL;
IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UX_SubmissionReceipts_PublicCode'
          AND object_id = OBJECT_ID(N'[dbo].[SubmissionReceipts]'))
    EXEC(N'CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicCode] ON [dbo].[SubmissionReceipts]([PublicCode]) WHERE [PublicCode] IS NOT NULL');

-- Rollback
-- 1. Turn the check off without a new build: set SubmissionDuplicates__Enabled to false
--    and restart the app. That stops new prompts and new same-day supersedes.
-- 2. Optional data undo, before dropping the audit table. This puts back only the
--    rows this feature marked Superseded. It does not touch Approved rows.
--    UPDATE c
--    SET c.RequestStatus = N'Submitted'
--    FROM dbo.HonoreeChangeRequests AS c
--    INNER JOIN dbo.SubmissionAuditEvents AS a ON a.ChangeRequestId = c.Id
--    WHERE a.Action = N'Superseded'
--      AND c.RequestStatus = N'Superseded'
--      AND NOT EXISTS (
--          SELECT 1 FROM dbo.SubmissionAuditEvents AS later
--          WHERE later.ChangeRequestId = c.Id AND later.Action = N'Restored' AND later.Id > a.Id);
-- 3. Drop only the objects this script added:
--    IF COL_LENGTH(N'dbo.HonoreeChangeRequests', N'CorrectsChangeRequestId') IS NOT NULL
--        ALTER TABLE [dbo].[HonoreeChangeRequests] DROP COLUMN [CorrectsChangeRequestId];
--    IF OBJECT_ID(N'[dbo].[SubmissionAuditEvents]', N'U') IS NOT NULL
--        DROP TABLE [dbo].[SubmissionAuditEvents];
--    IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
--        DROP TABLE [dbo].[SubmissionReceipts];
