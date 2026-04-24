-- ============================================================================
-- Enforce unique alias per connection on RPT_custom_primary_tables
--
-- The original UX_custom_primary_tables_unique index was on
-- (connection_id, table_name, alias) — so "EMPOWER.A AS X" and
-- "EMPOWER.B AS X" were both allowed. Admins requested that non-empty
-- aliases be globally unique within a connection so "X" always means
-- one thing throughout the app.
--
-- Empty alias is excluded from the constraint (filtered index) so
-- multiple rows with no alias can coexist — they don't share an identity
-- to collide on.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-21_custom_primary_tables_alias_unique_rollback.sql.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_custom_primary_tables_alias'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    CREATE UNIQUE INDEX UX_custom_primary_tables_alias
        ON EMPOWER.RPT_custom_primary_tables (connection_id, alias)
        WHERE alias <> '';
END
GO
