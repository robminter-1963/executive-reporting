-- Adds Individual / Distribution targeting to RPT_report_schedules.
-- Idempotent: every guard checks COL_LENGTH / sys.check_constraints so
-- re-running the migration on an already-patched DB is a no-op.
--
-- Behavioral note: the new `kind` column defaults to 'distribution' so
-- pre-existing schedule rows have a sane state without a backfill. The
-- Worker treats a 'distribution' row with a NULL dist_email as a
-- legacy owner-only schedule and falls through to schedule.owner_email
-- — keeping every currently-active schedule firing while admins re-save
-- through the new dialog. The fallback can be dropped in a follow-up
-- migration once every row has been touched.

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'kind') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules
        ADD kind NVARCHAR(20) NOT NULL CONSTRAINT DF_RPT_report_schedules_kind DEFAULT 'distribution';
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_id') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD team_id INT NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_connection_id') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD team_connection_id UNIQUEIDENTIFIER NULL;
GO

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'dist_email') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules ADD dist_email NVARCHAR(255) NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_report_schedules_kind'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules WITH NOCHECK
        ADD CONSTRAINT CK_RPT_report_schedules_kind
            CHECK (kind IN ('individual', 'distribution'));
END
GO
