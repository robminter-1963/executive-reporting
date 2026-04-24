-- ============================================================================
-- Role-scoped owner fields — the column that identifies "who owns a row"
-- varies by the user's role (Loan Officer filters by loan_officer_id,
-- Processor by processor_id, etc).
--
-- Replaces the single RPT_custom_primary_tables.owner_field_id introduced
-- in 2026-04-24_01-45 with a junction table RPT_primary_table_role_owners
-- keyed by (primary_table_id, role_id). A role with no entry for a given
-- primary → self-scoped queries against it return zero rows (ForceNoMatch).
-- Admins still bypass scoping entirely.
--
-- DATA MIGRATION: any existing owner_field_id values are NOT moved to the
-- new table automatically — the old model was "one column for everyone"
-- and the new model requires per-role assignment, so a silent copy would
-- give every role the same column which is exactly the ambiguity this
-- refactor exists to eliminate. If rows already have owner_field_id set,
-- re-assign per role via Admin → DB Connections → Table Aliases.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_primary_table_role_owners ───────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_primary_table_role_owners')
BEGIN
    CREATE TABLE EMPOWER.RPT_primary_table_role_owners (
        primary_table_id UNIQUEIDENTIFIER NOT NULL
                         REFERENCES EMPOWER.RPT_custom_primary_tables(id) ON DELETE CASCADE,
        role_id          UNIQUEIDENTIFIER NOT NULL
                         REFERENCES EMPOWER.RPT_roles(id) ON DELETE CASCADE,
        owner_field_id   NVARCHAR(128)    NOT NULL,
        created_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (primary_table_id, role_id)
    );

    CREATE INDEX IX_primary_table_role_owners_primary
        ON EMPOWER.RPT_primary_table_role_owners (primary_table_id);
END
GO

-- ── 2. Drop the old single-field column ────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables')
             AND name = 'owner_field_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables DROP COLUMN owner_field_id;
END
GO
