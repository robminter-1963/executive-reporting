-- Rollback for 2026-04-18_connection_table_filter.sql
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
             AND name = 'table_filter_sql')
    ALTER TABLE EMPOWER.RPT_company_connections DROP COLUMN table_filter_sql;
GO
