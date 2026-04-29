-- ============================================================================
-- Rollback for 2026-04-28_14-00_master_dashboard_sections.sql.
-- Drops the section_id FK + column, then the sections table. Idempotent.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_master_dashboard_tiles_section'
             AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
    DROP INDEX IX_master_dashboard_tiles_section ON EMPOWER.RPT_master_dashboard_tiles;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = 'FK_master_dashboard_tiles_section')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        DROP CONSTRAINT FK_master_dashboard_tiles_section;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
             AND name = 'section_id')
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP COLUMN section_id;
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER'
             AND TABLE_NAME = 'RPT_master_dashboard_sections')
    DROP TABLE EMPOWER.RPT_master_dashboard_sections;
GO
