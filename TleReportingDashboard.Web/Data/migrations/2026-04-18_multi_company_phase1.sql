-- ============================================================================
-- Phase 1: Multi-company data-model changes
--
-- Adds RPT_companies, RPT_user_companies, and a company_id column to every
-- user-scoped table. Seeds a DEFAULT company and backfills existing rows to
-- it. Keeps DEFAULT constraints on every new company_id column so existing
-- code that INSERTs without knowing about multi-company continues to work
-- until Phase 2 threads CompanyContext through every write path.
--
-- Idempotent: safe to re-run. Every DDL statement is gated by a catalog
-- check so partial runs can be retried without errors.
-- ============================================================================

-- ── Ensure EMPOWER schema exists (matches create_tables.sql pattern) ──────
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_companies — registry of companies served by the app ───────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_companies')
BEGIN
    CREATE TABLE EMPOWER.RPT_companies (
        id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        code              NVARCHAR(32)     NOT NULL UNIQUE,
        name              NVARCHAR(200)    NOT NULL,
        data_source_type  NVARCHAR(20)     NOT NULL,     -- 'sqlserver' | 'postgres' | ...
        connection_ref    NVARCHAR(100)    NOT NULL,     -- key into IConnectionStringProvider
        is_active         BIT              NOT NULL DEFAULT 1,
        created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ── 2. Seed the initial company — The Loan Exchange ─────────────────────
-- Fixed id so re-runs don't generate a new GUID. Every later step in this
-- script references the same constant. Code 'tle' becomes the URL segment
-- in Phase 2 (e.g. /c/tle/reports).
IF NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_companies WHERE code = 'tle')
BEGIN
    INSERT INTO EMPOWER.RPT_companies (id, code, name, data_source_type, connection_ref)
    VALUES (
        '00000000-0000-0000-0000-000000000001',
        'tle',
        'The Loan Exchange',
        'sqlserver',
        'Database-TLE'
    );
END
GO

-- ── 3. RPT_user_companies — maps users to companies with a permission ────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_user_companies')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_companies (
        user_id      NVARCHAR(128)    NOT NULL,
        company_id   UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
        permission   NVARCHAR(10)     NOT NULL CHECK (permission IN ('View', 'Edit')),
        is_default   BIT              NOT NULL DEFAULT 0,
        created_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (user_id, company_id)
    );
    CREATE INDEX IX_user_companies_user_id
        ON EMPOWER.RPT_user_companies (user_id);
    -- At most one default per user, enforced at the index layer
    CREATE UNIQUE INDEX UX_user_companies_default
        ON EMPOWER.RPT_user_companies (user_id)
        WHERE is_default = 1;
END
GO

-- ── 4. Backfill RPT_user_companies from every table with an owner/user ───
-- Union across all user-owning tables (not just saved_reports) so users who
-- only have preferences, tabs, or grid templates still get an Edit grant.
INSERT INTO EMPOWER.RPT_user_companies (user_id, company_id, permission, is_default)
SELECT DISTINCT u.user_id,
       '00000000-0000-0000-0000-000000000001',
       'Edit',
       1
FROM (
    SELECT owner_id AS user_id FROM EMPOWER.RPT_saved_reports
    UNION
    SELECT owner_id         FROM EMPOWER.RPT_report_schedules
    UNION
    SELECT owner_id         FROM EMPOWER.RPT_grid_templates
    UNION
    SELECT user_id          FROM EMPOWER.RPT_user_preferences
    UNION
    SELECT user_id          FROM EMPOWER.RPT_master_dashboard_tabs
    UNION
    SELECT user_id          FROM EMPOWER.RPT_master_dashboard_tiles
) u
WHERE u.user_id IS NOT NULL
  AND u.user_id <> 'SYSTEM'            -- skip the template-owner sentinel
  AND NOT EXISTS (
    SELECT 1 FROM EMPOWER.RPT_user_companies uc
    WHERE uc.user_id = u.user_id
      AND uc.company_id = '00000000-0000-0000-0000-000000000001'
);
GO

-- ── 5. Add company_id to every user-scoped table ─────────────────────────
-- Pattern per table:
--   1. ADD company_id UNIQUEIDENTIFIER NOT NULL with a DEFAULT of DEFAULT
--      company id — existing rows are backfilled in-place by SQL Server.
--   2. ADD foreign key to RPT_companies.
--   3. ADD a covering index that leads with company_id.
-- Defaults intentionally remain on every column. Phase 2 drops them once
-- CompanyContext is threaded through every write path.

-- 5.1 RPT_saved_reports
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_saved_reports_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_saved_reports_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD CONSTRAINT FK_saved_reports_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_company_owner'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    CREATE INDEX IX_saved_reports_company_owner
        ON EMPOWER.RPT_saved_reports (company_id, owner_id, is_template);
END
GO

-- 5.2 RPT_report_shares
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_report_shares')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_report_shares
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_report_shares_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_report_shares_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_report_shares
        ADD CONSTRAINT FK_report_shares_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_report_shares_company_report'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_report_shares'))
BEGIN
    CREATE INDEX IX_report_shares_company_report
        ON EMPOWER.RPT_report_shares (company_id, report_id);
END
GO

-- 5.3 RPT_report_schedules
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_report_schedules')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_report_schedules_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_report_schedules_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        ADD CONSTRAINT FK_report_schedules_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_report_schedules_company_owner'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
BEGIN
    CREATE INDEX IX_report_schedules_company_owner
        ON EMPOWER.RPT_report_schedules (company_id, owner_id, is_active);
END
GO

-- 5.4 RPT_grid_templates
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_grid_templates')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_grid_templates
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_grid_templates_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_grid_templates_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_grid_templates
        ADD CONSTRAINT FK_grid_templates_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_grid_templates_company_owner'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_grid_templates'))
BEGIN
    CREATE INDEX IX_grid_templates_company_owner
        ON EMPOWER.RPT_grid_templates (company_id, owner_id);
END
GO

-- 5.5 RPT_master_dashboard_tabs
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_master_dashboard_tabs_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tabs_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs
        ADD CONSTRAINT FK_master_dashboard_tabs_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tabs_company_user'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs'))
BEGIN
    CREATE INDEX IX_master_dashboard_tabs_company_user
        ON EMPOWER.RPT_master_dashboard_tabs (company_id, user_id);
END
GO

-- 5.6 RPT_master_dashboard_tiles
-- Two columns here: company_id (the owning dashboard's company) AND
-- source_company_id (the company whose data the tile pulls from). For now
-- both default to DEFAULT; Phase 4 lets admins repoint source_company_id.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_master_dashboard_tiles_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
                 AND name = 'source_company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD source_company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_master_dashboard_tiles_source_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tiles_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD CONSTRAINT FK_master_dashboard_tiles_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tiles_source_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD CONSTRAINT FK_master_dashboard_tiles_source_company
        FOREIGN KEY (source_company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tiles_company_tab'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
BEGIN
    CREATE INDEX IX_master_dashboard_tiles_company_tab
        ON EMPOWER.RPT_master_dashboard_tiles (company_id, tab_id);
END
GO

-- 5.7 RPT_user_preferences (PK change from (user_id) to (user_id, company_id))
-- Existing rows become the user's preferences for the DEFAULT company.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
                 AND name = 'company_id')
BEGIN
    -- Drop the old PK so we can rebuild it as composite
    DECLARE @prefsPk NVARCHAR(200) = (
        SELECT name FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
          AND type = 'PK'
    );
    IF @prefsPk IS NOT NULL
        EXEC('ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT ' + @prefsPk);

    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_user_preferences_company
            DEFAULT '00000000-0000-0000-0000-000000000001';

    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD CONSTRAINT PK_user_preferences PRIMARY KEY (user_id, company_id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_user_preferences_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD CONSTRAINT FK_user_preferences_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO

-- ── 6. RPT_schema_config — per-company schema ────────────────────────────
-- Today the table has a singleton row (id = 1). After this step each company
-- owns exactly one row, keyed by company_id. Drop the singleton check, drop
-- the old INT id, add company_id, backfill the existing row to DEFAULT, and
-- make company_id the new PK.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
                 AND name = 'company_id')
BEGIN
    -- Drop singleton check
    IF EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_schema_config_singleton'
                 AND parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config'))
    BEGIN
        ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT CK_schema_config_singleton;
    END

    -- Drop the old PK (its name is auto-generated so we have to look it up)
    DECLARE @schemaPk NVARCHAR(200) = (
        SELECT name FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
          AND type = 'PK'
    );
    IF @schemaPk IS NOT NULL
        EXEC('ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT ' + @schemaPk);

    -- Add company_id with default so the existing row is backfilled in-place
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_schema_config_company
            DEFAULT '00000000-0000-0000-0000-000000000001';

    -- Drop the now-redundant INT id column
    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config') AND name = 'id')
    BEGIN
        ALTER TABLE EMPOWER.RPT_schema_config DROP COLUMN id;
    END

    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT PK_schema_config PRIMARY KEY (company_id);

    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT FK_schema_config_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO

-- ── 7. RPT_schema_config_history — audit trail, one row per save ─────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config_history
        ADD company_id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_schema_config_history_company
            DEFAULT '00000000-0000-0000-0000-000000000001';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_history_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config_history
        ADD CONSTRAINT FK_schema_config_history_company
        FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_schema_config_history_company_updated'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history'))
BEGIN
    CREATE INDEX IX_schema_config_history_company_updated
        ON EMPOWER.RPT_schema_config_history (company_id, updated_at DESC);
END
GO
