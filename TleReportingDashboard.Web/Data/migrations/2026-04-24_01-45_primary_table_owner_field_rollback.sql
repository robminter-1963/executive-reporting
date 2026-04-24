-- ============================================================================
-- Rollback — drop owner_field_id from RPT_custom_primary_tables.
-- Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'owner_field_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN owner_field_id;
END
GO
