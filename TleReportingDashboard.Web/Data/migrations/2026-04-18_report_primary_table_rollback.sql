IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
             AND name = 'primary_table')
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN primary_table;
GO
