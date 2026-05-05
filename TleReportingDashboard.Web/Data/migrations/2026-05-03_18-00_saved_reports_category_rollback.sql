-- Rollback: drop the index then the column. Both guarded so the script
-- is safe to re-run.

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_category'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    DROP INDEX IX_saved_reports_category ON EMPOWER.RPT_saved_reports;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
             AND name = 'category')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN category;
END
GO
