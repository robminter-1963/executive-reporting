-- Rollback for 2026-05-20_company_kpis.sql.
-- Drops the show_kpi_band column from RPT_companies, then the KPI table.
-- Idempotent: skips each drop when the object is already gone.

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
             AND name = 'show_kpi_band')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_RPT_companies_show_kpi_band')
    BEGIN
        ALTER TABLE EMPOWER.RPT_companies DROP CONSTRAINT DF_RPT_companies_show_kpi_band;
    END
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN show_kpi_band;
END
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER'
             AND TABLE_NAME = 'RPT_company_kpis')
BEGIN
    DROP TABLE EMPOWER.RPT_company_kpis;
END
GO
