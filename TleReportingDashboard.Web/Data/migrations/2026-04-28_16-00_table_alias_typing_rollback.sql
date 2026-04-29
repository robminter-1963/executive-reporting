-- ============================================================================
-- Rollback for 2026-04-28_16-00_table_alias_typing.sql.
-- Drops the three columns added to RPT_custom_primary_tables. Idempotent.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'additional_key_columns')
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN additional_key_columns;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'primary_column')
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN primary_column;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'table_type')
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN table_type;
GO
