-- ─────────────────────────────────────────────────────────────────────────────
-- In-app notifications inbox.
--
-- Powers the bell icon + dropdown in MainLayout. Producers (sharing,
-- scheduled reports, admin announcements) write rows here keyed by
-- user_email; the UI reads + marks-read by id. Email-keyed (not user_id-
-- keyed) so pre-provisioned users who haven't signed in yet still
-- accumulate a backlog they'll see on first sign-in.
--
-- Idempotent: re-runs cleanly on databases that already have the table.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_user_notifications' AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_notifications (
        id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_user_notifications_id    DEFAULT NEWID(),
        user_email          NVARCHAR(255)    NOT NULL,
        -- Kind discriminator — drives the icon + how the click navigates.
        -- Free-text by design so producers can add new kinds without a
        -- schema change. UI falls back to a generic icon when unknown.
        kind                NVARCHAR(64)     NOT NULL,
        title               NVARCHAR(200)    NOT NULL,
        body                NVARCHAR(1000)   NULL,
        link_url            NVARCHAR(500)    NULL,
        related_entity_type NVARCHAR(32)     NULL,
        related_entity_id   NVARCHAR(64)     NULL,
        is_read             BIT              NOT NULL CONSTRAINT DF_user_notifications_read  DEFAULT 0,
        created_at          DATETIME2        NOT NULL CONSTRAINT DF_user_notifications_created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_user_notifications PRIMARY KEY (id)
    );
END
GO

-- Composite index optimized for the "what's new for this user" read path:
--   WHERE user_email = @e AND is_read = 0 ORDER BY created_at DESC
-- The DESC ordering is materialized so the bell-dropdown query is a clean
-- index seek + range scan with no sort step.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_user_notifications_user_unread'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_user_notifications'))
BEGIN
    CREATE INDEX IX_user_notifications_user_unread
        ON EMPOWER.RPT_user_notifications(user_email, is_read, created_at DESC);
END
GO
