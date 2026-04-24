-- ============================================================================
-- Rollback for 2026-04-22_custom_primary_tables_flags.sql
-- Drops the filtered unique index + both flag columns + their DEFAULT
-- constraints. Idempotent: safe to re-run.
-- ============================================================================

-- ── 1. Drop the filtered unique index first ────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'UX_custom_primary_tables_one_default'
             AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    DROP INDEX UX_custom_primary_tables_one_default
        ON EMPOWER.RPT_custom_primary_tables;
END
GO

-- ── 2. Drop the is_default_primary column + its DEFAULT constraint ─────
IF COL_LENGTH('EMPOWER.RPT_custom_primary_tables', 'is_default_primary') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_custom_primary_tables_is_default_primary')
    BEGIN
        ALTER TABLE EMPOWER.RPT_custom_primary_tables
            DROP CONSTRAINT DF_custom_primary_tables_is_default_primary;
    END
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        DROP COLUMN is_default_primary;
END
GO

-- ── 3. Drop the is_primary column + its DEFAULT constraint ─────────────
IF COL_LENGTH('EMPOWER.RPT_custom_primary_tables', 'is_primary') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_custom_primary_tables_is_primary')
    BEGIN
        ALTER TABLE EMPOWER.RPT_custom_primary_tables
            DROP CONSTRAINT DF_custom_primary_tables_is_primary;
    END
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        DROP COLUMN is_primary;
END
GO
