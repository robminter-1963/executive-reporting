-- Rollback: 2026-05-09_10-00_app_settings.sql

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER'
             AND TABLE_NAME = 'RPT_app_settings')
BEGIN
    DROP TABLE EMPOWER.RPT_app_settings;
END
GO
