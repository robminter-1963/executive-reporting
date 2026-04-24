-- ============================================================================
-- RPT_companies — add logo storage
--
-- Stores the company's branding logo as inline bytes. Currently unused by
-- the rest of the app ("for future use") — the Admin → Companies tab is
-- the only writer. When a future feature wants to render it (tile header,
-- master dashboard chrome, email template, etc.), it reads these two
-- columns:
--
--   logo              VARBINARY(MAX)  — raw image bytes
--   logo_content_type NVARCHAR(50)    — MIME type ("image/png" etc.)
--
-- Idempotent. Paired with 2026-04-23_22-30_company_logo_rollback.sql.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_companies', 'logo') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD logo VARBINARY(MAX) NULL;
END
GO

IF COL_LENGTH('EMPOWER.RPT_companies', 'logo_content_type') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD logo_content_type NVARCHAR(50) NULL;
END
GO
