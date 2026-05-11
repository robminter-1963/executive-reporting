-- ─────────────────────────────────────────────────────────────────────────────
-- Per-company theme support.
--
-- Adds company_id (NULL = global default) to RPT_app_theme. The existing
-- single seeded row stays unchanged with company_id = NULL — service-side
-- read path falls back to it whenever a per-company theme isn't set, so
-- environments without per-company customization keep rendering the same
-- palette as before.
--
-- Companies that want their own palette get one row per company, looked up
-- by (company_id). The unique filtered index enforces "at most one
-- per-company row" while still permitting the single id=1 global row
-- to coexist (its company_id is NULL, excluded by the filter).
--
-- Idempotent: re-runs cleanly.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_app_theme')
                 AND name = 'company_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_app_theme
        ADD company_id UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_app_theme_company'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_app_theme'))
BEGIN
    CREATE UNIQUE INDEX UX_app_theme_company
        ON EMPOWER.RPT_app_theme(company_id)
        WHERE company_id IS NOT NULL;
END
GO
