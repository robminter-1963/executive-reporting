-- ============================================================================
-- Rollback for 2026-04-21_connection_schema_filter.sql
--
-- Drops schema_filter_sql. The companion table_filter_sql stays intact.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
             AND name = 'schema_filter_sql')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        DROP COLUMN schema_filter_sql;
END
GO
