-- ─────────────────────────────────────────────────────────────────────────────
-- Per-user dashboard tab visibility.
--
-- Tabs themselves remain shared at the company level (RPT_master_dashboard_tabs).
-- This table is a pure view filter: a row means "this user has hidden this
-- tab in their view of the company's dashboard." Absence of a row = visible
-- (the default for every user). Admin/edit mode bypasses this filter so the
-- canonical layout is always editable.
--
-- Cascading delete on tab_id so admin-removed tabs don't leave dangling
-- per-user rows. No FK on user_id (matches the existing pattern — user rows
-- aren't FK targets across the schema; user_id is the Entra OID string).
--
-- Idempotent: skips creation when the table already exists.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables
               WHERE name = 'RPT_user_hidden_dashboard_tabs'
                 AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_hidden_dashboard_tabs (
        user_id   NVARCHAR(255) NOT NULL,
        tab_id    INT           NOT NULL,
        hidden_at DATETIME2     NOT NULL
            CONSTRAINT DF_user_hidden_dashboard_tabs_hidden_at DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_user_hidden_dashboard_tabs PRIMARY KEY (user_id, tab_id),
        CONSTRAINT FK_user_hidden_dashboard_tabs_tab
            FOREIGN KEY (tab_id)
            REFERENCES EMPOWER.RPT_master_dashboard_tabs (id)
            ON DELETE CASCADE
    );

    -- Reverse-direction lookup ("which users have hidden this tab?") isn't
    -- on the hot path, but the index supports the cascade above efficiently
    -- and makes future per-tab cleanup queries cheap.
    CREATE INDEX IX_user_hidden_dashboard_tabs_tab_id
        ON EMPOWER.RPT_user_hidden_dashboard_tabs (tab_id);
END
GO
