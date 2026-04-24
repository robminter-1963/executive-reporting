-- ============================================================================
-- Rollback for 2026-04-22_connection_pg_display_timezone.sql
-- Drops the pg_display_timezone column. Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'pg_display_timezone') IS NOT NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        DROP COLUMN pg_display_timezone;
END
GO
