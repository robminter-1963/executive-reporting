-- ============================================================================
-- RPT_custom_primary_tables — per-connection saved root tables with aliases
--
-- The Report Builder's Primary Table picker defaults to the distinct
-- SourceTable values in the connection's schema. Users (or later, admins)
-- can also save arbitrary custom roots for reuse — a full schema.table_name
-- plus a short alias the SQL emitter uses in the FROM clause.
--
-- Stored shared per-connection (not per-user) so the set of valid roots is a
-- team-level catalog. Permission gating is application-layer for now; a
-- future admin-only mode can enforce via service-level checks.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-21_custom_primary_tables_rollback.sql.
-- ============================================================================

-- ── 1. Create the table ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_custom_primary_tables')
BEGIN
    CREATE TABLE EMPOWER.RPT_custom_primary_tables (
        id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        connection_id   UNIQUEIDENTIFIER NOT NULL
                        REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
        table_name      NVARCHAR(200)    NOT NULL,                 -- e.g. 'EMPOWER.LN_MTGTERMS'
        alias           NVARCHAR(60)     NOT NULL DEFAULT '',      -- e.g. 'L'  (used as FROM alias; empty = no alias)
        created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        created_by_id   NVARCHAR(255)    NULL,       -- Entra oid of creator
        created_by_email NVARCHAR(255)   NULL
    );
END
GO

-- ── 2. Indexes ──────────────────────────────────────────────────────────
-- Fast dropdown lookup per connection.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_custom_primary_tables_connection'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    CREATE INDEX IX_custom_primary_tables_connection
        ON EMPOWER.RPT_custom_primary_tables (connection_id, table_name, alias);
END
GO

-- Disallow duplicate (table, alias) combinations per connection — they'd be
-- indistinguishable in the picker. Case-insensitive collation handles
-- "EMPOWER.TBL AS L" vs "empower.tbl AS l".
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_custom_primary_tables_unique'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_custom_primary_tables'))
BEGIN
    CREATE UNIQUE INDEX UX_custom_primary_tables_unique
        ON EMPOWER.RPT_custom_primary_tables (connection_id, table_name, alias);
END
GO
