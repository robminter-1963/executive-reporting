-- ─────────────────────────────────────────────────────────────────────────────
-- App-wide theme tokens.
--
-- Single-row table — admins tune one global palette via Admin → Theme. The
-- payload is a JSON document with ~14 named tokens (surface-page,
-- text-primary, accent-primary, etc.); MainLayout reads it on every render
-- and injects a `:root { --token: value; }` block so all chrome CSS that
-- references `var(--…)` picks up the current theme.
--
-- Per-company overrides + dark mode are out of scope for v1. The id column
-- exists so a future schema bump can add additional theme rows (e.g. one
-- for light + one for dark) without breaking the single-row contract.
--
-- Idempotent: re-runs cleanly on databases that already have the table or
-- the seeded row.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables
               WHERE name = 'RPT_app_theme'
                 AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    CREATE TABLE EMPOWER.RPT_app_theme (
        id          INT             NOT NULL CONSTRAINT PK_app_theme PRIMARY KEY,
        json        NVARCHAR(MAX)   NOT NULL,
        updated_by  NVARCHAR(255)   NULL,
        updated_at  DATETIME2       NOT NULL CONSTRAINT DF_app_theme_updated_at DEFAULT SYSUTCDATETIME()
    );
END
GO

-- Seed the default light-mode palette so the service has something to read
-- even before an admin saves anything. Values match the hex literals
-- currently hard-coded across the chrome surfaces, so applying the seeded
-- theme is a visual no-op vs. the prior build.
IF NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_app_theme WHERE id = 1)
BEGIN
    INSERT INTO EMPOWER.RPT_app_theme (id, json, updated_by)
    VALUES (1, N'{
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
    }', 'bootstrap');
END
GO
