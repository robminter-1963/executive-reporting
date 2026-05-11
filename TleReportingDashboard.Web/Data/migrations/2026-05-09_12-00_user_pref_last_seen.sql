-- ─────────────────────────────────────────────────────────────────────────────
-- Add last_master_dashboard_seen to RPT_user_preferences so the landing
-- greeting can compute "X reports updated since you last visited."
-- Stored UTC; read once on Master Dashboard mount, then bumped to NOW.
-- Idempotent.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_user_preferences')
                 AND name = 'last_master_dashboard_seen')
BEGIN
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD last_master_dashboard_seen DATETIME NULL;
END
GO
