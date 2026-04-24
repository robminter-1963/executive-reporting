-- ============================================================================
-- Phase 1 rollback — reverses 2026-04-18_multi_company_phase1.sql
--
-- Drops FKs, company_id columns, new indexes, and the two new tables.
-- Order matters: drop FKs first, then indexes, then columns, then tables.
-- Each step is gated by a catalog check so the script is idempotent.
-- ============================================================================

-- ── 1. Drop FKs pointing at RPT_companies ────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_history_company')
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP CONSTRAINT FK_schema_config_history_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_company')
    ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT FK_schema_config_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_user_preferences_company')
    ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT FK_user_preferences_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tiles_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP CONSTRAINT FK_master_dashboard_tiles_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tiles_source_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP CONSTRAINT FK_master_dashboard_tiles_source_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_master_dashboard_tabs_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs DROP CONSTRAINT FK_master_dashboard_tabs_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_grid_templates_company')
    ALTER TABLE EMPOWER.RPT_grid_templates DROP CONSTRAINT FK_grid_templates_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_report_schedules_company')
    ALTER TABLE EMPOWER.RPT_report_schedules DROP CONSTRAINT FK_report_schedules_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_report_shares_company')
    ALTER TABLE EMPOWER.RPT_report_shares DROP CONSTRAINT FK_report_shares_company;
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_saved_reports_company')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT FK_saved_reports_company;
GO

-- ── 2. Drop the new indexes ──────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_schema_config_history_company_updated'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history'))
    DROP INDEX IX_schema_config_history_company_updated ON EMPOWER.RPT_schema_config_history;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_master_dashboard_tiles_company_tab'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
    DROP INDEX IX_master_dashboard_tiles_company_tab ON EMPOWER.RPT_master_dashboard_tiles;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_master_dashboard_tabs_company_user'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs'))
    DROP INDEX IX_master_dashboard_tabs_company_user ON EMPOWER.RPT_master_dashboard_tabs;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_grid_templates_company_owner'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_grid_templates'))
    DROP INDEX IX_grid_templates_company_owner ON EMPOWER.RPT_grid_templates;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_report_schedules_company_owner'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
    DROP INDEX IX_report_schedules_company_owner ON EMPOWER.RPT_report_schedules;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_report_shares_company_report'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_report_shares'))
    DROP INDEX IX_report_shares_company_report ON EMPOWER.RPT_report_shares;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_saved_reports_company_owner'
                                       AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    DROP INDEX IX_saved_reports_company_owner ON EMPOWER.RPT_saved_reports;
GO

-- ── 3. Revert RPT_schema_config to its singleton shape ───────────────────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config') AND name = 'company_id')
BEGIN
    -- Drop the new PK
    DECLARE @schemaPk NVARCHAR(200) = (
        SELECT name FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config') AND type = 'PK'
    );
    IF @schemaPk IS NOT NULL
        EXEC('ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT ' + @schemaPk);

    -- Drop default + column
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_schema_config_company')
        ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT DF_schema_config_company;
    ALTER TABLE EMPOWER.RPT_schema_config DROP COLUMN company_id;

    -- Re-add the INT id column, singleton check, and PK
    ALTER TABLE EMPOWER.RPT_schema_config ADD id INT NOT NULL DEFAULT 1;
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT CK_schema_config_singleton CHECK (id = 1);
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT PK_schema_config PRIMARY KEY (id);
END
GO

-- ── 4. Drop company_id from RPT_schema_config_history ────────────────────
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_schema_config_history_company')
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP CONSTRAINT DF_schema_config_history_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_schema_config_history DROP COLUMN company_id;
GO

-- ── 5. Restore RPT_user_preferences PK to (user_id) ──────────────────────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_preferences') AND name = 'company_id')
BEGIN
    DECLARE @prefsPk NVARCHAR(200) = (
        SELECT name FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_user_preferences') AND type = 'PK'
    );
    IF @prefsPk IS NOT NULL
        EXEC('ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT ' + @prefsPk);

    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_user_preferences_company')
        ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT DF_user_preferences_company;
    ALTER TABLE EMPOWER.RPT_user_preferences DROP COLUMN company_id;

    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD CONSTRAINT PK_user_preferences PRIMARY KEY (user_id);
END
GO

-- ── 6. Drop company_id from remaining tables ─────────────────────────────
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_master_dashboard_tiles_source_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP CONSTRAINT DF_master_dashboard_tiles_source_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles') AND name = 'source_company_id')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP COLUMN source_company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_master_dashboard_tiles_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP CONSTRAINT DF_master_dashboard_tiles_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP COLUMN company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_master_dashboard_tabs_company')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs DROP CONSTRAINT DF_master_dashboard_tabs_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs DROP COLUMN company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_grid_templates_company')
    ALTER TABLE EMPOWER.RPT_grid_templates DROP CONSTRAINT DF_grid_templates_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_grid_templates') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_grid_templates DROP COLUMN company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_report_schedules_company')
    ALTER TABLE EMPOWER.RPT_report_schedules DROP CONSTRAINT DF_report_schedules_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_report_schedules') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_report_shares_company')
    ALTER TABLE EMPOWER.RPT_report_shares DROP CONSTRAINT DF_report_shares_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_report_shares') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_report_shares DROP COLUMN company_id;

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_saved_reports_company')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT DF_saved_reports_company;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports') AND name = 'company_id')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN company_id;
GO

-- ── 7. Drop the two new tables ───────────────────────────────────────────
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_user_companies')
    DROP TABLE EMPOWER.RPT_user_companies;
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_companies')
    DROP TABLE EMPOWER.RPT_companies;
GO
