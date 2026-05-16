-- ─────────────────────────────────────────────────────────────────────────────
-- Reset the global app-theme row to pick up the new Linear-style defaults.
--
-- Updates AppTheme.Default() in code (indigo accent, off-white surfaces, slate
-- neutrals, white toolbar) ship the new palette as the class-level defaults
-- — but the DB row seeded by 2026-05-04_10-00_app_theme.sql still holds the
-- prior Google-Workspace palette. Setting json to '{}' makes the AppTheme
-- deserializer fall back to every property's class-defined default value,
-- which now reflects the new design system.
--
-- Per-company override rows (where they exist) are NOT touched — admins who
-- customized a tenant theme keep their overrides. Only the global fallback
-- changes.
--
-- Idempotent: safe to re-run. Rollback restores the original seeded JSON
-- byte-for-byte.
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE EMPOWER.RPT_app_theme
SET    json       = N'{}',
       updated_by = 'linear-redesign',
       updated_at = SYSUTCDATETIME()
WHERE  id = 1;
GO
