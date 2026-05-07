-- ─────────────────────────────────────────────────────────────────────────────
-- Library Sections — admin-curated sub-categories for grouping reports in the
-- Report Library's "All Reports" tab. Mirrors the Master Dashboard's per-tab
-- sections concept: a named, sortable, optional bucket. Reports without a
-- section land in a catch-all bucket at render time.
--
-- Two changes:
--   1. RPT_library_sections — sections per company with sort_order so admins
--      can pre-create empty sections and arrange them.
--   2. RPT_saved_reports.library_section_id — FK (NULL = catch-all bucket).
--
-- ON DELETE SET NULL on the FK so deleting a section never orphans reports —
-- they just fall back to the catch-all.
--
-- Idempotent: re-runs cleanly on databases that already have the table /column.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER'
                 AND TABLE_NAME = 'RPT_library_sections')
BEGIN
    CREATE TABLE EMPOWER.RPT_library_sections (
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RPT_library_sections PRIMARY KEY,
        company_id UNIQUEIDENTIFIER NOT NULL,
        name NVARCHAR(200) NOT NULL,
        sort_order INT NOT NULL DEFAULT 0,
        is_active BIT NOT NULL DEFAULT 1,
        created_at DATETIME NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_RPT_library_sections_company
            FOREIGN KEY (company_id)
            REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE
    );
END
GO

-- Lookup index for the typical "list active sections for company X in order" query.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_library_sections_company_sort'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_library_sections'))
BEGIN
    CREATE INDEX IX_library_sections_company_sort
        ON EMPOWER.RPT_library_sections(company_id, sort_order, name);
END
GO

-- Per-company unique section name (case-insensitive via the default collation
-- on a freshly-created column). Two sections with the same name in the same
-- company would render ambiguously in the Library; reject at the DB layer so
-- the admin gets a clear error instead of confusing UI.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_library_sections_company_name'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_library_sections'))
BEGIN
    CREATE UNIQUE INDEX UX_library_sections_company_name
        ON EMPOWER.RPT_library_sections(company_id, name)
        WHERE is_active = 1;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'library_section_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD library_section_id UNIQUEIDENTIFIER NULL;
END
GO

-- ON DELETE SET NULL — when a section is deleted, reports stay (uncategorized).
-- The named constraint lets the rollback drop it cleanly.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_saved_reports_library_section')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD CONSTRAINT FK_saved_reports_library_section
            FOREIGN KEY (library_section_id)
            REFERENCES EMPOWER.RPT_library_sections(id)
            ON DELETE SET NULL;
END
GO

-- Filtered index — most reports start uncategorized; filtering on NOT NULL
-- keeps the index narrow.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_library_section'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    CREATE INDEX IX_saved_reports_library_section
        ON EMPOWER.RPT_saved_reports(library_section_id)
        WHERE library_section_id IS NOT NULL;
END
GO
