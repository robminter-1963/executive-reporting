-- ============================================================================
-- Saved reports — admin-facing internal_name.
--
-- Reports that target the same audience but differ by data slice (e.g., one
-- "Pipeline" per loan type) tend to share the public Name so the dashboard
-- tile reads cleanly across variants. That same shared Name made the Master
-- Dashboard's "Add Report" picker ambiguous — admins couldn't tell which
-- report they were pinning. internal_name is the per-variant disambiguator
-- shown in the picker; it falls back to Name when blank, so existing reports
-- keep working without a backfill.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'internal_name')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD internal_name NVARCHAR(200) NULL;
END
GO
