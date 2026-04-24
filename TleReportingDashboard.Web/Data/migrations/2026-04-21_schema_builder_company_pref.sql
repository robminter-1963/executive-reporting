-- ============================================================================
-- Remember the admin's last-selected Schema Builder company
--
-- Companion to schema_builder_connection_id. Storing the company id
-- explicitly lets the page restore the outer picker even when the user
-- hasn't yet picked a connection (e.g., the company has no connections
-- configured) — otherwise we'd have no way to persist intent.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-21_schema_builder_company_pref_rollback.sql.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'schema_builder_company_id') IS NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD schema_builder_company_id UNIQUEIDENTIFIER NULL;
GO
