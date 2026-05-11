-- ─────────────────────────────────────────────────────────────────────────────
-- App Settings — generic key/value table for admin-configurable values that
-- aren't tied to a specific entity (companies, connections, schedules) and
-- that ops would otherwise have to set in appsettings.json.
--
-- First consumer: WorkerDashboardUrl — the per-environment URL of the
-- Hangfire worker dashboard. Admins set it on the Admin → App Settings tab;
-- MainLayout reads it to show / hide the "Worker Jobs" link in the user menu.
--
-- Rationale: appsettings.json is dev-controlled. App Settings put runtime
-- knobs in admin hands without a redeploy.
--
-- Idempotent: re-runs cleanly on databases that already have the table.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER'
                 AND TABLE_NAME = 'RPT_app_settings')
BEGIN
    CREATE TABLE EMPOWER.RPT_app_settings (
        [key] NVARCHAR(100) NOT NULL CONSTRAINT PK_RPT_app_settings PRIMARY KEY,
        [value] NVARCHAR(MAX) NULL,
        updated_at DATETIME NOT NULL DEFAULT GETDATE(),
        updated_by NVARCHAR(256) NULL
    );
END
GO
