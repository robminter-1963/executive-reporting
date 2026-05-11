-- ─────────────────────────────────────────────────────────────────────────────
-- Per-user favorited reports. Drives:
--   • Star toggle in the Report Library (row-level)
--   • "Pinned" strip above the Master Dashboard tabs (personal shortcut bar,
--     distinct from the admin-curated tiles)
--
-- Composite PK on (user_id, report_id) so a user can only favorite a given
-- report once. ON DELETE CASCADE on report_id removes the favorite when the
-- underlying report is deleted (no orphan stars). user_id is NVARCHAR
-- because RPT_users is keyed by Entra object id (string), matching every
-- other per-user table in the schema.
--
-- sort_order is reserved for future drag-reorder UX; defaults to 0 today
-- (Pinned strip just sorts by created_at desc as v1).
--
-- Idempotent.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER'
                 AND TABLE_NAME = 'RPT_user_favorites')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_favorites (
        user_id    NVARCHAR(255)    NOT NULL,
        report_id  UNIQUEIDENTIFIER NOT NULL,
        sort_order INT              NOT NULL DEFAULT 0,
        created_at DATETIME         NOT NULL DEFAULT GETDATE(),
        CONSTRAINT PK_RPT_user_favorites PRIMARY KEY (user_id, report_id),
        CONSTRAINT FK_user_favorites_report
            FOREIGN KEY (report_id)
            REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_user_favorites_user'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_user_favorites'))
BEGIN
    CREATE INDEX IX_user_favorites_user
        ON EMPOWER.RPT_user_favorites(user_id, sort_order, created_at);
END
GO
