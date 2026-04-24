-- ============================================================================
-- Rollback — removes RPT_companies.website_url added by
-- 2026-04-24_00-05_company_website_url.sql.
-- Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
             AND name = 'website_url')
BEGIN
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN website_url;
END
GO
