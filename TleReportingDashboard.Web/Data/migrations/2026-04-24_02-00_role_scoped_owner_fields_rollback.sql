-- ============================================================================
-- Rollback — role-scoped owner fields.
-- Drops the junction table and restores the single column (nullable) on
-- RPT_custom_primary_tables. Data in the junction table is NOT migrated
-- back into the single column; admin re-assigns via the UI if needed.
-- Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_primary_table_role_owners')
BEGIN
    DROP TABLE EMPOWER.RPT_primary_table_role_owners;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
                 AND name = 'owner_field_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD owner_field_id NVARCHAR(128) NULL;
END
GO
