-- ─────────────────────────────────────────────────────────────────────────────
-- Rollback: 2026-05-08_14-00_library_sections.sql
-- Drops the FK + filtered index + library_section_id column on saved_reports,
-- then drops the RPT_library_sections table.
-- ─────────────────────────────────────────────────────────────────────────────

IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = 'FK_saved_reports_library_section')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        DROP CONSTRAINT FK_saved_reports_library_section;
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_saved_reports_library_section'
             AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    DROP INDEX IX_saved_reports_library_section ON EMPOWER.RPT_saved_reports;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
             AND name = 'library_section_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN library_section_id;
END
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER'
             AND TABLE_NAME = 'RPT_library_sections')
BEGIN
    DROP TABLE EMPOWER.RPT_library_sections;
END
GO
