-- ============================================================================
-- Add ss_mars to RPT_company_connections
--
-- MultipleActiveResultSets toggle, SQL Server only. Null / 0 = off (the
-- driver default), 1 = append "MultipleActiveResultSets=true" to the
-- connection string. Admins can flip it per-connection from the DB
-- Connections editor; most connections should leave it off since the
-- query pipeline uses single-reader-per-connection throughout.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-22_connection_mars_rollback.sql.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_company_connections')
                 AND name = 'ss_mars')
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD ss_mars BIT NULL;
END
GO
