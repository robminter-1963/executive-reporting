-- ─────────────────────────────────────────────────────────────────────────────
-- Theme tokens for data tables + detail-view group rows.
--
-- Additive — the AppTheme C# model deserializes existing rows just fine
-- thanks to default values on every property, so this migration only
-- bumps the seed JSON for fresh databases. Environments that already
-- have a saved theme keep their values; tokens we just added fall
-- through to the C# defaults until an admin sets them via Admin → Theme.
--
-- Idempotent: only updates the seed row if its payload is still the
-- exact bootstrap value. An admin who already saved a customized theme
-- doesn't get clobbered.
-- ─────────────────────────────────────────────────────────────────────────────

IF EXISTS (SELECT 1 FROM EMPOWER.RPT_app_theme
           WHERE id = 1 AND updated_by = 'bootstrap')
BEGIN
    UPDATE EMPOWER.RPT_app_theme
       SET json = N'{
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
            "accentError":     "#D32F2F",
            "tableHeaderBg":           "#F7F7F7",
            "tableRowHover":           "#F1F3F4",
            "tableRowStriped":         "#FAFAFA",
            "detailGroupHeaderBg":     "#FFEFD5",
            "detailGroupHeaderText":   "#202124",
            "detailGroupFooterBg":     "#F5F5DC",
            "detailGroupFooterText":   "#202124",
            "detailGrandTotalBg":      "#F7F7F7",
            "detailGrandTotalText":    "#202124"
        }',
        updated_at = SYSUTCDATETIME()
     WHERE id = 1 AND updated_by = 'bootstrap';
END
GO
