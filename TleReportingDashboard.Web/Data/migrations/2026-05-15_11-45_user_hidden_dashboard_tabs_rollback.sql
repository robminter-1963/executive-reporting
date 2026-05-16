-- ─────────────────────────────────────────────────────────────────────────────
-- Rollback: drop the per-user hidden-tabs table.
-- Idempotent — skips when the table is already gone.
-- ─────────────────────────────────────────────────────────────────────────────

IF EXISTS (SELECT 1 FROM sys.tables
           WHERE name = 'RPT_user_hidden_dashboard_tabs'
             AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    DROP TABLE EMPOWER.RPT_user_hidden_dashboard_tabs;
END
GO
