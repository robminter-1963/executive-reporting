-- ============================================================================
-- Saved-reports company_id backfill.
--
-- Bug: ReportDbService.SaveReportAsync / UpdateReportAsync historically
-- omitted company_id from their INSERT/UPDATE column lists, so every report
-- created or saved before today's fix landed in RPT_saved_reports with
-- company_id = NULL. The Master Dashboard's "Add Report" picker filters on
-- that column — NULL rows never matched, so flagging "Show on Master
-- Dashboard" had no visible effect for affected reports.
--
-- The save path now derives company_id server-side from the report's
-- connection. This migration backfills any existing rows that were left
-- with NULL company_id, joining through RPT_company_connections to recover
-- the right value. Reports with no connection_id stay NULL — those are
-- legacy rows that wouldn't have a company anyway.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

UPDATE r
   SET r.company_id = c.company_id
  FROM EMPOWER.RPT_saved_reports r
  JOIN EMPOWER.RPT_company_connections c ON c.id = r.connection_id
 WHERE r.company_id IS NULL
   AND r.connection_id IS NOT NULL;
GO
