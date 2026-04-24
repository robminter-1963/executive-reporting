-- ============================================================================
-- Rollback for 2026-04-18_company_connections.sql
--
-- Drops RPT_company_connections. RPT_companies is untouched — its
-- connection_ref column was never modified.
-- ============================================================================

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_company_connections')
BEGIN
    DROP TABLE EMPOWER.RPT_company_connections;
END
GO
