-- ─────────────────────────────────────────────────────────────────────────────
-- Phase-1.1 extension to RPT_company_kpis — adds three optional filter inputs
-- the original phase didn't cover:
--   1. filters NVARCHAR(MAX) NULL — JSON dict of {fieldId: value} for ad-hoc
--      "where X = Y" predicates (e.g. status = Funded, loan_type = Conv).
--   2. custom_filter_ids NVARCHAR(MAX) NULL — JSON array of schema custom-
--      filter ids the KPI opts in to (mirrors RPT_saved_reports column_state's
--      CustomFilterIds list).
--   3. date_from / date_to DATETIME2 NULL — explicit from/to dates used when
--      period = 'custom'. Lets admins lock a KPI to an exact range (e.g.
--      a fiscal-quarter window that doesn't align to MTD/QTD/YTD).
--
-- Also widens the period CHECK constraint to accept 'custom'.
--
-- Idempotent: each ADD COLUMN / constraint swap guards against re-runs.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis')
                 AND name = 'filters')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis ADD filters NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis')
                 AND name = 'custom_filter_ids')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis ADD custom_filter_ids NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis')
                 AND name = 'date_from')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis ADD date_from DATETIME2 NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_kpis')
                 AND name = 'date_to')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis ADD date_to DATETIME2 NULL;
END
GO

-- Widen the period CHECK constraint to accept 'custom'. Drop + recreate so
-- existing rows aren't reinterpreted differently mid-flight (none can be
-- 'custom' yet — the column is freshly nullable + the CHECK rejected it).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RPT_company_kpis_period')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_kpis DROP CONSTRAINT CK_RPT_company_kpis_period;
END
GO

ALTER TABLE EMPOWER.RPT_company_kpis ADD CONSTRAINT CK_RPT_company_kpis_period
    CHECK (period IS NULL OR period IN ('mtd','qtd','ytd','last_30d','last_90d','custom'));
GO
