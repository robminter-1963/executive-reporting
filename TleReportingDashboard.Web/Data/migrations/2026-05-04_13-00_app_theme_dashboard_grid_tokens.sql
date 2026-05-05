-- ─────────────────────────────────────────────────────────────────────────────
-- Theme tokens for the data grid INSIDE each Master Dashboard tile.
--
-- The prior "dashboardHeader/Footer" tokens paint the tile chrome (title
-- bar + metadata strip). These four govern the actual data table — its
-- sticky <thead> column-header row and sticky totals <tfoot> row.
--
-- Additive — admin-saved themes pass through untouched (the C# defaults
-- backfill these keys when missing). Bumps only the bootstrap-seed row
-- so a fresh DB lands with the full payload.
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
            "detailGrandTotalText":    "#202124",
            "dashboardHeaderBg":       "#FFFFFF",
            "dashboardHeaderText":     "#202124",
            "dashboardFooterBg":       "#FAFAFA",
            "dashboardFooterText":     "#5F6368",
            "dashboardGridHeaderBg":   "#ADD8E6",
            "dashboardGridHeaderText": "#202124",
            "dashboardGridFooterBg":   "#FFF8DC",
            "dashboardGridFooterText": "#202124"
        }',
        updated_at = SYSUTCDATETIME()
     WHERE id = 1 AND updated_by = 'bootstrap';
END
GO
