-- Rollback: 2026-05-09_15-00_app_theme_per_company.sql

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'UX_app_theme_company'
             AND object_id = OBJECT_ID('EMPOWER.RPT_app_theme'))
BEGIN
    DROP INDEX UX_app_theme_company ON EMPOWER.RPT_app_theme;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_app_theme')
             AND name = 'company_id')
BEGIN
    -- Per-company rows would be orphaned if any exist; drop them so the
    -- column drop succeeds.
    DELETE FROM EMPOWER.RPT_app_theme WHERE company_id IS NOT NULL;
    ALTER TABLE EMPOWER.RPT_app_theme DROP COLUMN company_id;
END
GO
