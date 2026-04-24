-- Adds rich trigger + SSRS-style recipient columns to RPT_report_schedules.
-- Idempotent: guarded by COL_LENGTH checks.

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'schedule_pattern') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD schedule_pattern NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'start_date') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD start_date DATETIME2 NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'end_date') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD end_date DATETIME2 NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'recipients') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD recipients NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'cc_recipients') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD cc_recipients NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'bcc_recipients') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD bcc_recipients NVARCHAR(MAX) NULL;
GO
