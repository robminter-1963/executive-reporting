-- ============================================================================
-- Add schema_filter_sql to RPT_company_connections
--
-- Companion to table_filter_sql. Some deployments keep reporting-relevant
-- tables in a handful of named schemas (e.g. EMPOWER, RPT) and don't want
-- the full INFORMATION_SCHEMA list. This column stores a separate WHERE
-- fragment that targets TABLE_SCHEMA; the service AND's it with the
-- existing table_filter_sql so admins can narrow on both axes.
--
-- Typical values:
--   TABLE_SCHEMA = 'EMPOWER'
--   TABLE_SCHEMA IN ('EMPOWER', 'RPT')
--
-- SQL-injection note: admin-editable text that ends up in SQL. Gated by
-- the Admin hub; no end-user input ever reaches this column.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-21_connection_schema_filter_rollback.sql.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
                 AND name = 'schema_filter_sql')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD schema_filter_sql NVARCHAR(MAX) NULL;
END
GO
