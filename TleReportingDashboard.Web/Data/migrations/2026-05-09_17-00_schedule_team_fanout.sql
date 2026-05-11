-- Adds the team-fanout strategy column to RPT_report_schedules.
--
-- Background: Individual schedules previously fanned out one email per
-- team member. Some report owners want the report sent to the team's
-- manager instead (or to both manager + members). team_fanout drives
-- the new branch in the Worker — relevant only when kind='individual'.
-- Distribution schedules ignore this column.
--
-- Allowed values:
--   'members' — existing behavior, one personalized export per team
--               member, scope-filtered by member's ext_id (default).
--   'manager' — single email to the team's manager, scope-filtered by
--               manager_ext_id so the manager sees their team's roll-up.
--               Bypasses the scope filter when the manager has all-access
--               (admin-tier role).
--   'both'    — manager AND every member, each with the appropriate
--               scope. Manager + N member emails per fire.
--
-- Idempotent.

IF COL_LENGTH('EMPOWER.RPT_report_schedules', 'team_fanout') IS NULL
    ALTER TABLE EMPOWER.RPT_report_schedules
        ADD team_fanout NVARCHAR(20) NOT NULL
            CONSTRAINT DF_RPT_report_schedules_team_fanout DEFAULT 'members';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_RPT_report_schedules_team_fanout'
       AND parent_object_id = OBJECT_ID('EMPOWER.RPT_report_schedules'))
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules WITH NOCHECK
        ADD CONSTRAINT CK_RPT_report_schedules_team_fanout
            CHECK (team_fanout IN ('members', 'manager', 'both'));
END
GO
