-- ============================================================================
-- RPT_master_dashboard_personal_tiles — per-user pins on top of the shared
-- canonical layout.
--
-- Design: every user can pin reports to their own personal layer that
-- renders alongside the company's shared tiles. Their pins are visible only
-- to them; the shared layout is unchanged. Solves the "non-admin report
-- owner wants their own report on a tab" gap without unlocking the shared
-- layout to non-admins.
--
-- Row visibility = (user_id, company_id, tab_id) tuple. The MasterDashboard
-- loads shared + personal tiles for the active tab, merges them by
-- sort_order, and renders. Personal tiles get a small visual indicator so
-- the user knows they're theirs (and admins-viewing-as-themselves know
-- which tiles are personal vs canonical).
--
-- FK choices:
--   * company_id     → CASCADE: company gone, personal pins gone.
--   * tab_id         → CASCADE: tab gone, personal pins on it gone.
--   * report_id      → CASCADE: report gone, personal pins gone.
--   * section_id     → NO ACTION (NULL on delete handled in app code, same
--                       pattern as shared tiles — SQL Server refuses
--                       two cascade paths via tab_id and section_id).
--
-- user_id is NVARCHAR(128) to match RPT_user_companies — supports both the
-- real Entra OID after first sign-in AND the email-stub used for pre-
-- provisioning before the user has signed in.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_master_dashboard_personal_tiles')
BEGIN
    CREATE TABLE EMPOWER.RPT_master_dashboard_personal_tiles (
        id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        user_id         NVARCHAR(128)    NOT NULL,
        company_id      UNIQUEIDENTIFIER NOT NULL
                        REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
        tab_id          INT              NOT NULL
                        REFERENCES EMPOWER.RPT_master_dashboard_tabs(id) ON DELETE CASCADE,
        report_id       UNIQUEIDENTIFIER NOT NULL
                        REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
        sort_order      INT              NOT NULL DEFAULT 0,
        col_span        INT              NOT NULL DEFAULT 12,
        height          INT              NOT NULL DEFAULT 500,
        title_align     NVARCHAR(10)     NULL,
        section_id      INT              NULL
                        REFERENCES EMPOWER.RPT_master_dashboard_sections(id) ON DELETE NO ACTION,
        created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );

    -- Hot path: dashboard render fetches by (user, company, tab).
    CREATE INDEX IX_personal_tiles_user_company_tab
        ON EMPOWER.RPT_master_dashboard_personal_tiles (user_id, company_id, tab_id, sort_order);

    -- Dedup guard: same user can't pin the same report twice on the same
    -- tab. Mirror of the shared-tile uniqueness rule, scoped per user.
    CREATE UNIQUE INDEX UX_personal_tiles_user_tab_report
        ON EMPOWER.RPT_master_dashboard_personal_tiles (user_id, tab_id, report_id);
END;
GO
