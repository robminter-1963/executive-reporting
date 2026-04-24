-- ============================================================================
-- Rollback for 2026-04-21_custom_primary_tables_alias_unique.sql
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'UX_custom_primary_tables_alias'
             AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    DROP INDEX UX_custom_primary_tables_alias ON EMPOWER.RPT_custom_primary_tables;
END
GO
