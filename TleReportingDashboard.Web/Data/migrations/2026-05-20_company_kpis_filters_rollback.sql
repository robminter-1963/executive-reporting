-- Rollback for 2026-05-20_company_kpis_filters.sql. Restores the narrower
-- CHECK constraint and drops the new columns. Safe to re-run (each step
-- skips when the object is already gone).

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RPT_company_kpis_period')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP CONSTRAINT CK_RPT_company_kpis_period;
END
GO

-- Reset any rows that opted into the new period before rolling back, so the
-- narrowed CHECK can recreate cleanly.
UPDATE EMPOWER.RPT_company_kpis SET period = NULL WHERE period = 'custom';
GO

ALTER TABLE EMPOWER.RPT_company_kpis ADD CONSTRAINT CK_RPT_company_kpis_period
    CHECK (period IS NULL OR period IN ('mtd','qtd','ytd','last_30d','last_90d'));
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis') AND name = 'date_to')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP COLUMN date_to;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis') AND name = 'date_from')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP COLUMN date_from;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis') AND name = 'custom_filter_ids')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP COLUMN custom_filter_ids;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis') AND name = 'filters')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP COLUMN filters;
END
GO
