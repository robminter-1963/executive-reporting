-- ============================================================================
-- Remember the user's last-selected Report Library company
--
-- Scopes which company's reports the Library shows. Persisted per user so
-- the view sticks across sessions and devices. Null = "All companies" (the
-- unfiltered default).
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-22_report_library_company_pref_rollback.sql.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'report_library_company_id') IS NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD report_library_company_id UNIQUEIDENTIFIER NULL;
GO
