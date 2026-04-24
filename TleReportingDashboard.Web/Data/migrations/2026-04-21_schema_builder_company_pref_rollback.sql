-- ============================================================================
-- Rollback for 2026-04-21_schema_builder_company_pref.sql
--
-- Drops schema_builder_company_id. The companion schema_builder_connection_id
-- is left intact — it pre-dates this migration.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'schema_builder_company_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        DROP COLUMN schema_builder_company_id;
GO
