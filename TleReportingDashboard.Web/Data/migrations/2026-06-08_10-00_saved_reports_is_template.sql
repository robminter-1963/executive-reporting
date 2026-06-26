-- ─────────────────────────────────────────────────────────────────────────────
-- Adds an `is_template` BIT to RPT_saved_reports so admins can flag a report
-- as a starter template. Templates show up in the Report Library's "Templates"
-- tab for every user (no explicit share required) and the "Use this template"
-- button clones them into the user's own reports as an editable starting point.
--
-- Default 0 = back-compat: every existing report stays a normal report. Admins
-- opt rows in via the per-row toggle on the Library page.
--
-- Filtered index because templates are a tiny fraction of all saved reports;
-- the index covers the "Templates" tab's SELECT WHERE is_template = 1 without
-- bloating the table's general index footprint.
--
-- Idempotent: re-runs cleanly on databases that already have the column.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'is_template')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD is_template BIT NOT NULL CONSTRAINT DF_RPT_saved_reports_is_template DEFAULT 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_is_template'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    CREATE INDEX IX_saved_reports_is_template
        ON EMPOWER.RPT_saved_reports(is_template)
        WHERE is_template = 1;
END
GO
