-- ============================================================================
-- Scope grid templates to a connection
--
-- Templates reference field ids from the schema catalog. Field catalogs are
-- per-connection (RPT_schema_config is keyed on connection_id), so a template
-- built against one connection's schema is generally not valid against a
-- different connection. Adding connection_id lets the Apply Template dialog
-- filter to only the templates relevant to the report's current connection.
--
-- Existing rows are backfilled to the TLE company's default connection so
-- the pre-existing templates keep working against the TLE schema they were
-- authored for.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_grid_templates')
                 AND name = 'connection_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_grid_templates
        ADD connection_id UNIQUEIDENTIFIER NULL;
END
GO

-- Backfill: point all existing rows at the TLE default connection. Done in a
-- separate statement so re-runs only touch NULL rows.
DECLARE @TleDefaultConnectionId UNIQUEIDENTIFIER = (
    SELECT TOP 1 id
    FROM EMPOWER.RPT_company_connections
    WHERE company_id = '00000000-0000-0000-0000-000000000001'  -- TLE
      AND is_default = 1
);

IF @TleDefaultConnectionId IS NOT NULL
BEGIN
    UPDATE EMPOWER.RPT_grid_templates
    SET connection_id = @TleDefaultConnectionId
    WHERE connection_id IS NULL;
END
GO
