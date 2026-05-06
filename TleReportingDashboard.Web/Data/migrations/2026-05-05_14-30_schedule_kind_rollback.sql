-- Rollback for 2026-05-05_14-30_schedule_kind.sql.
-- Drops the kind / team / dist columns and the CHECK constraint.
-- The Worker's owner-email fallback path keeps existing schedules firing
-- after rollback, so this is safe even on rows that were re-saved with
-- the new dialog (the new fields just go away; owner_email remains).

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_report_schedules_kind'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
    ALTER TABLE EMPOWER.RPT_report_schedules
        DROP CONSTRAINT CK_RPT_report_schedules_kind;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'dist_email') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN dist_email;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_connection_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN team_connection_id;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN team_id;
GO

-- Drop the default constraint before the column to keep MSSQL happy.
IF EXISTS (
    SELECT 1 FROM sys.default_constraints
     WHERE name = 'DF_RPT_report_schedules_kind')
    ALTER TABLE EMPOWER.RPT_report_schedules
        DROP CONSTRAINT DF_RPT_report_schedules_kind;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'kind') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN kind;
GO
