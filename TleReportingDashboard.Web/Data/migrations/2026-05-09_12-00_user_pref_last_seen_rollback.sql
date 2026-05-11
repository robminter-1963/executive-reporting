-- Rollback: 2026-05-09_12-00_user_pref_last_seen.sql

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
             AND name = 'last_master_dashboard_seen')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_preferences
        DROP COLUMN last_master_dashboard_seen;
END
GO
