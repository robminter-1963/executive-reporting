-- ─────────────────────────────────────────────────────────────────────────────
-- Adds an optional `category` column to RPT_saved_reports so admins can group
-- reports for end-user discoverability (Library filter + chip on rows).
--
-- Free-text by design — no foreign key to a category catalog. Picking a list
-- of canonical names is a UX call admins should make per company; an
-- autocomplete in the Builder UI surfaces existing values to nudge consistency
-- without enforcing it.
--
-- Idempotent: re-runs cleanly on databases that already have the column.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'category')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD category NVARCHAR(64) NULL;
END
GO

-- Indexed so the Library filter query (WHERE category = @c) and the
-- autocomplete distinct-values query both stay snappy as report counts grow.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_category'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    CREATE INDEX IX_saved_reports_category
        ON EMPOWER.RPT_saved_reports(category)
        WHERE category IS NOT NULL;
END
GO
