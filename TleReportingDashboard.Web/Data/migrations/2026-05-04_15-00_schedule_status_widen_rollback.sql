-- Rollback — narrow last_run_status back to NVARCHAR(50). Will fail if any
-- existing row is longer than 50; admin needs to clear/trim those first.

IF EXISTS (
    SELECT 1
      FROM sys.columns c
      JOIN sys.tables t ON t.object_id = c.object_id
     WHERE t.name = 'RPT_report_schedules'
       AND SCHEMA_NAME(t.schema_id) = 'EMPOWER'
       AND c.name = 'last_run_status'
)
BEGIN
    ALTER TABLE EMPOWER.RPT_report_schedules
        ALTER COLUMN last_run_status NVARCHAR(50) NULL;
END
GO
