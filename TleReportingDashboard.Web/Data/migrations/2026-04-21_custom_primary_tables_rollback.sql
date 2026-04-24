-- ============================================================================
-- Rollback for 2026-04-21_custom_primary_tables.sql
--
-- Drops RPT_custom_primary_tables. Reports that saved a "schema.table AS
-- alias" combined string in RPT_saved_reports.primary_table are unaffected —
-- the alias form is emitter-agnostic and continues to work without this table.
-- ============================================================================

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_custom_primary_tables')
BEGIN
    DROP TABLE EMPOWER.RPT_custom_primary_tables;
END
GO
