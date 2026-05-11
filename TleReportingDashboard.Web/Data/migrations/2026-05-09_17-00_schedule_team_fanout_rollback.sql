-- Rollback: 2026-05-09_17-00_schedule_team_fanout.sql

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_report_schedules_team_fanout'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        DROP CONSTRAINT CK_RPT_report_schedules_team_fanout;
END
GO

IF EXISTS (
    SELECT 1 FROM sys.default_constraints
     WHERE name = 'DF_RPT_report_schedules_team_fanout'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        DROP CONSTRAINT DF_RPT_report_schedules_team_fanout;
END
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_fanout') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_report_schedules DROP COLUMN team_fanout;
GO
