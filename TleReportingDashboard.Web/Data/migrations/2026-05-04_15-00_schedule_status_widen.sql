-- ─────────────────────────────────────────────────────────────────────────────
-- Widens RPT_report_schedules.last_run_status from NVARCHAR(50) to (500).
--
-- The job writes "Failed: <ex.Message>" into this column on every failed run;
-- 50 characters wasn't enough room to capture even a typical exception
-- prefix ("Primary Table is required..." truncates mid-word). 500 fits a
-- sentence-and-a-half — long enough that admins can read what went wrong
-- without tailing logs, short enough to avoid bloating the table.
--
-- Idempotent: only runs the ALTER if the column is currently narrower than
-- 500 wide chars. Re-running on a DB that's already migrated is a no-op.
-- ─────────────────────────────────────────────────────────────────────────────

IF EXISTS (
    SELECT 1
      FROM sys.columns c
      JOIN sys.tables t ON t.object_id = c.object_id
     WHERE t.name = 'RPT_report_schedules'
       AND SCHEMA_NAME(t.schema_id) = 'EMPOWER'
       AND c.name = 'last_run_status'
       AND c.max_length < 1000   -- nvarchar bytes = chars * 2; <1000 = <500 chars
)
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        ALTER COLUMN last_run_status NVARCHAR(500) NULL;
END
GO
