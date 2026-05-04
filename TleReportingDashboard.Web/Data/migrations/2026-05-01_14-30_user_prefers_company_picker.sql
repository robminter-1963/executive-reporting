-- ============================================================================
-- Adds RPT_users.prefers_company_picker — a sticky "default to the company
-- picker" preference. Separate from last_visited_company_id because the latter
-- gets re-set on every Master Dashboard load (including browser-restored URLs
-- and bookmarks), which would otherwise clobber an explicit "All Companies"
-- choice the moment the user navigates to a company page for any reason.
--
-- Set TRUE when the user lands on /?all=1 (the explicit "show me the picker"
-- URL). Set FALSE when the user picks a specific company FROM the picker —
-- that's a deliberate switch back to per-company defaults. Direct URL
-- navigation to /master-dashboard/<code> doesn't touch this flag, so the
-- preference survives bookmarks / session restore.
--
-- Auto-redirect logic in CompanyPicker short-circuits when this is TRUE,
-- showing the picker even if last_visited_company_id is set.
--
-- Idempotent: COL_LENGTH guards on the ALTER.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF COL_LENGTH('EMPOWER.RPT_users', 'prefers_company_picker') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_users
        ADD prefers_company_picker BIT NOT NULL CONSTRAINT DF_RPT_users_prefers_company_picker DEFAULT 0;
END
GO
