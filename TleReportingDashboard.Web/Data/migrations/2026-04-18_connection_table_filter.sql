-- ============================================================================
-- Add table_filter_sql to RPT_company_connections
--
-- Schema Builder's "Browse Tables" used to hard-code a SQL Server LIKE filter
-- specific to the Empower LN_ / U_LN_ naming convention. That's not portable
-- across connection types or deployments — each connection needs its own
-- filter fragment.
--
-- The column stores a raw SQL WHERE-fragment that the service appends after
-- `WHERE TABLE_TYPE = 'BASE TABLE' AND ` when listing tables. An empty value
-- means "no filter" (all base tables returned).
--
-- SQL-injection note: this is admin-editable text that ends up in SQL.
-- Only global admins can write here (gated by the Admin hub). No end-user
-- input ever reaches this column.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
                 AND name = 'table_filter_sql')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD table_filter_sql NVARCHAR(MAX) NULL;
END
GO

-- Seed the TLE primary connection with the legacy Empower filter so the
-- existing Schema Builder behavior is preserved on deploy.
UPDATE EMPOWER.RPT_company_connections
SET table_filter_sql = N'(TABLE_NAME LIKE ''LN[_]%'' OR TABLE_NAME LIKE ''U[_]LN[_]%'')'
WHERE company_id = '00000000-0000-0000-0000-000000000001'
  AND name = 'primary'
  AND table_filter_sql IS NULL;
GO
