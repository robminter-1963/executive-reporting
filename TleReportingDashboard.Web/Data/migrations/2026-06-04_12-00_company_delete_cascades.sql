-- Widen NO ACTION → ON DELETE CASCADE on the company_id FKs that SQL
-- Server permits (single cascade path). Most company_id FKs already had
-- CASCADE from the start; the original NO ACTION choices were defensive
-- ("never accidentally delete a company"), but now that DeleteCompanyAsync
-- is the deliberate destructive path, tight FKs are a safety net so a
-- direct DELETE (or a future cascade we forget about) can't leave orphans.
--
-- Tables LEFT at NO ACTION because of SQL Server's multi-cascade-path
-- restriction (single table can't have two CASCADE paths to the same
-- parent — e.g. saved_reports already cascades from companies via
-- connections → connection_id; adding CASCADE on company_id would
-- create a second path):
--   * RPT_saved_reports         — cascades via connection_id
--   * RPT_report_shares         — cascades via report_id (which cascades from connections)
--   * RPT_report_schedules      — cascades via report_id
--   * RPT_grid_templates        — cascades via connection_id
--   * RPT_master_dashboard_tabs — referenced by tiles + sections
--   * RPT_master_dashboard_tiles — has two FKs to companies (company_id + source_company_id); CASCADE on either creates a multi-path conflict with the personal_tiles / sections / tabs chain
--   * RPT_schema_config         — connection-keyed PK; cascades via connection_id
-- The application-level DeleteCompanyAsync deletes these explicitly in
-- dependency order before the company row drops.

-- ── RPT_user_preferences ──
-- Single path (only FK to companies). Safe to cascade.
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
       AND referenced_object_id = OBJECT_ID('EMPOWER.RPT_companies'))
BEGIN
    DECLARE @fk_user_prefs NVARCHAR(256) = (
        SELECT name FROM sys.foreign_keys
         WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
           AND referenced_object_id = OBJECT_ID('EMPOWER.RPT_companies'));
    IF @fk_user_prefs IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE EMPOWER.RPT_user_preferences DROP CONSTRAINT ' + @fk_user_prefs);
        ALTER TABLE EMPOWER.RPT_user_preferences
            ADD CONSTRAINT FK_RPT_user_preferences_companies
            FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE;
    END;
END;
GO

-- ── RPT_schema_config_history ──
-- Single path. Audit trail rows should drop when the company drops.
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history')
       AND referenced_object_id = OBJECT_ID('EMPOWER.RPT_companies'))
BEGIN
    DECLARE @fk_schema_hist NVARCHAR(256) = (
        SELECT name FROM sys.foreign_keys
         WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history')
           AND referenced_object_id = OBJECT_ID('EMPOWER.RPT_companies'));
    IF @fk_schema_hist IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE EMPOWER.RPT_schema_config_history DROP CONSTRAINT ' + @fk_schema_hist);
        ALTER TABLE EMPOWER.RPT_schema_config_history
            ADD CONSTRAINT FK_RPT_schema_config_history_companies
            FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE;
    END;
END;
GO
