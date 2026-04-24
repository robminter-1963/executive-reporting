-- ============================================================================
-- Adds RPT_companies.website_url — optional outbound link surfaced on the
-- master dashboard (clicking the company logo opens this URL in a new tab).
-- Editable from Admin → Companies. NULL means "no link" and the logo
-- simply won't be clickable.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
                 AND name = 'website_url')
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD website_url NVARCHAR(500) NULL;
END
GO
