-- ============================================================================
-- Rollback for 2026-04-23_22-30_company_logo.sql
-- Drops the logo + logo_content_type columns. Idempotent.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_companies', 'logo_content_type') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN logo_content_type;
END
GO

IF COL_LENGTH('EMPOWER.RPT_companies', 'logo') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies DROP COLUMN logo;
END
GO
