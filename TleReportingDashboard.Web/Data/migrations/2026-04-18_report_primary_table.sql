-- ============================================================================
-- Add primary_table override to RPT_saved_reports
--
-- Each report can override the schema's default FROM table. Null means
-- "use the schema's Settings.PrimaryTable". Useful when a single connection
-- hosts reports rooted at different tables (e.g. lead vs opportunity
-- against a Salesforce Postgres schema).
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'primary_table')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD primary_table NVARCHAR(500) NULL;
END
GO
