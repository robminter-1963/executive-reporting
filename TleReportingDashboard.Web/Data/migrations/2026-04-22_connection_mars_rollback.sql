-- ============================================================================
-- Rollback for 2026-04-22_connection_mars.sql
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
             AND name = 'ss_mars')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        DROP COLUMN ss_mars;
END
GO
