-- ============================================================================
-- Rollback for 2026-04-22_report_library_company_pref.sql
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'report_library_company_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        DROP COLUMN report_library_company_id;
GO
