-- Rollback for 2026-04-18_report_connection.sql
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_connection'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
    DROP INDEX IX_saved_reports_connection ON EMPOWER.RPT_saved_reports;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_saved_reports_connection')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT FK_saved_reports_connection;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports') AND name = 'connection_id')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN connection_id;
GO
