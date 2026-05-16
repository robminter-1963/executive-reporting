-- ─────────────────────────────────────────────────────────────────────────────
-- Rollback: restore the original seeded JSON from 2026-05-04_10-00_app_theme.sql.
--
-- Reverses the Linear-redesign theme reset by writing the pre-redesign palette
-- (Google-Workspace neutrals + Google-blue accent) back into the global row.
-- Use this when AppTheme.Default() in code has ALSO been reverted to the old
-- values; otherwise the DB and code will disagree (DB old, code new) and the
-- DB wins on render — leaving the UI on the old palette until a fresh reset.
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE EMPOWER.RPT_app_theme
SET    json = N'{
        "surfacePage":     "#FAFAFA",
        "surfaceToolbar":  "#3C4043",
        "surfaceCard":     "#FFFFFF",
        "surfaceStrip":    "#E8EAED",
        "textPrimary":     "#202124",
        "textSecondary":   "#5F6368",
        "textMuted":       "#9AA0A6",
        "textOnToolbar":   "#E8EAED",
        "borderDefault":   "#DADCE0",
        "borderSubtle":    "#E8EAED",
        "accentPrimary":   "#1A73E8",
        "accentSuccess":   "#1E8E3E",
        "accentWarning":   "#F9AB00",
        "accentError":     "#D32F2F"
    }',
       updated_by = 'linear-redesign-rollback',
       updated_at = SYSUTCDATETIME()
WHERE  id = 1;
GO
