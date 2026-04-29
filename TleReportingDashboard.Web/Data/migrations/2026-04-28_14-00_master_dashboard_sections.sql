-- ============================================================================
-- Master Dashboard Sections — sub-grouping under each tab.
--
-- Adds RPT_master_dashboard_sections (one row per named section, scoped to a
-- tab) and a nullable section_id FK on RPT_master_dashboard_tiles. NULL on a
-- tile means the tile renders under the "(no section)" header at the top of
-- its tab — preserves the current single-bucket layout for any tab whose
-- admin hasn't created sections yet.
--
-- Deletion semantics:
--   • Tab deleted  → sections cascade (this FK).
--   • Section deleted → tiles' section_id is cleared by the application code
--     (RemoveSectionAsync issues an UPDATE first), then the section row is
--     deleted. Using NO ACTION on the tiles.section_id FK avoids the
--     multi-cascade-path warning SQL Server raises when a single delete
--     could reach RPT_master_dashboard_tiles via two routes (the existing
--     tab->tiles app-level DELETE plus a hypothetical section SET NULL).
--
-- Idempotent: every DDL guarded by a catalog check; safe to re-run.
-- ============================================================================

-- ── 1. RPT_master_dashboard_sections ────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER'
                 AND TABLE_NAME = 'RPT_master_dashboard_sections')
BEGIN
    CREATE TABLE EMPOWER.RPT_master_dashboard_sections (
        id           INT IDENTITY(1,1) NOT NULL
                         CONSTRAINT PK_master_dashboard_sections PRIMARY KEY,
        tab_id       INT             NOT NULL,
        label        NVARCHAR(100)   NOT NULL,
        sort_order   INT             NOT NULL,
        title_align  VARCHAR(10)     NOT NULL
                         CONSTRAINT DF_master_dashboard_sections_align DEFAULT('left'),
        collapsed    BIT             NOT NULL
                         CONSTRAINT DF_master_dashboard_sections_collapsed DEFAULT(0),
        CONSTRAINT FK_master_dashboard_sections_tab
            FOREIGN KEY (tab_id)
            REFERENCES EMPOWER.RPT_master_dashboard_tabs(id) ON DELETE CASCADE
    );
END
GO

-- ── 2. Lookup index — driven by the per-tab read pattern ────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_sections_tab_sort'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_sections'))
BEGIN
    CREATE INDEX IX_master_dashboard_sections_tab_sort
        ON EMPOWER.RPT_master_dashboard_sections (tab_id, sort_order);
END
GO

-- ── 3. RPT_master_dashboard_tiles.section_id ────────────────────────────────
-- Nullable on purpose. NULL means "(no section)" — the existing layout.
-- No backfill: every existing tile keeps section_id = NULL, so a tab with
-- zero sections renders identically to today.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
                 AND name = 'section_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD section_id INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_master_dashboard_tiles_section')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD CONSTRAINT FK_master_dashboard_tiles_section
            FOREIGN KEY (section_id)
            REFERENCES EMPOWER.RPT_master_dashboard_sections(id);
END
GO

-- ── 4. Lookup index for tiles by section ────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tiles_section'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
BEGIN
    CREATE INDEX IX_master_dashboard_tiles_section
        ON EMPOWER.RPT_master_dashboard_tiles (section_id);
END
GO
