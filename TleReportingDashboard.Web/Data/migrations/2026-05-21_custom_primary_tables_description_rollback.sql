-- Rollback for 2026-05-21_custom_primary_tables_description.sql.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'description')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN description;
END
GO
