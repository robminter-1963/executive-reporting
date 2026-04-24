-- ============================================================================
-- Rollback for 2026-04-18_admins.sql — drops RPT_admins.
-- appsettings Admins.Emails is unaffected and remains the fallback.
-- ============================================================================

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_admins')
BEGIN
    DROP TABLE EMPOWER.RPT_admins;
END
GO
