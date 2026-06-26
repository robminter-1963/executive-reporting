-- Revert 2026-06-08_10-00_saved_reports_is_template.sql.
-- Drops the filtered index, then the default constraint, then the column.

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_is_template'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    DROP INDEX IX_saved_reports_is_template ON EMPOWER.RPT_saved_reports;
END
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints
           WHERE name = 'DF_RPT_saved_reports_is_template')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT DF_RPT_saved_reports_is_template;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
             AND name = 'is_template')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN is_template;
END
GO
