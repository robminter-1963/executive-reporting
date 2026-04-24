-- ============================================================================
-- RPT_custom_primary_tables — add primary curation flags
--
-- Two new columns:
--   is_primary          → this row is eligible to serve as a report's primary
--                         table. Surfaces it in the Report Builder's
--                         "Suggested primaries" group (starred). All other
--                         rows remain selectable under "Other tables".
--   is_default_primary  → exactly one row per connection can carry this flag.
--                         New reports pre-select it. Enforced with a filtered
--                         unique index below.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-22_custom_primary_tables_flags_rollback.sql.
-- ============================================================================

-- ── 1. Add is_primary column ────────────────────────────────────────────
IF COL_LENGTH('EMPOWER.RPT_custom_primary_tables', 'is_primary') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD is_primary BIT NOT NULL CONSTRAINT DF_custom_primary_tables_is_primary DEFAULT (0);
END
GO

-- ── 2. Add is_default_primary column ────────────────────────────────────
IF COL_LENGTH('EMPOWER.RPT_custom_primary_tables', 'is_default_primary') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_custom_primary_tables
        ADD is_default_primary BIT NOT NULL CONSTRAINT DF_custom_primary_tables_is_default_primary DEFAULT (0);
END
GO

-- ── 3. Filtered unique index: only one default per connection ──────────
-- A partial UNIQUE index over is_default_primary = 1 is the DB-level enforcer;
-- the service layer also clears prior defaults on set, but this catches any
-- out-of-band writes (migrations, direct SQL, future code paths).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_custom_primary_tables_one_default'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    CREATE UNIQUE INDEX UX_custom_primary_tables_one_default
        ON EMPOWER.RPT_custom_primary_tables (connection_id)
        WHERE is_default_primary = 1;
END
GO
