-- Rollback for 2026-06-04_10-00_report_batches. Drops in reverse FK order:
-- access + items first (they FK to batches), batches last.

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batch_access' AND schema_id = SCHEMA_ID('EMPOWER'))
    DROP TABLE EMPOWER.RPT_report_batch_access;
GO

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batch_items' AND schema_id = SCHEMA_ID('EMPOWER'))
    DROP TABLE EMPOWER.RPT_report_batch_items;
GO

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_report_batches' AND schema_id = SCHEMA_ID('EMPOWER'))
    DROP TABLE EMPOWER.RPT_report_batches;
GO
