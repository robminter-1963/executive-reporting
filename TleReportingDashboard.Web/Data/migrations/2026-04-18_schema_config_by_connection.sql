-- ============================================================================
-- Re-key RPT_schema_config and RPT_schema_config_history on connection_id
--
-- Each connection now carries its own schema (fields, joins, lookups,
-- custom filters, settings). Reports load schema for the connection they
-- were saved against. Different connections — even inside the same company
-- — can have totally different DB schemas (e.g., a warehouse vs. an OLTP).
--
-- Backfill: every existing schema row is tied to a company. We move it to
-- that company's default/primary connection so nothing breaks on deploy.
--
-- Idempotent: safe to re-run. Non-destructive on columns (we add the new
-- column, backfill, then swap the PK; the old company_id column stays for
-- rollback safety until a follow-up migration drops it).
-- ============================================================================

-- ── 1. schema_config: add connection_id ──────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
                 AND name = 'connection_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD connection_id UNIQUEIDENTIFIER NULL;
END
GO

-- ── 2. Backfill: pick each company's default/first-active connection ────
UPDATE s
SET s.connection_id = c.id
FROM EMPOWER.RPT_schema_config s
CROSS APPLY (
    SELECT TOP 1 id
    FROM EMPOWER.RPT_company_connections
    WHERE company_id = s.company_id AND is_active = 1
    ORDER BY is_default DESC, name
) c
WHERE s.connection_id IS NULL;
GO

-- ── 3. Swap the PK from (company_id) to (connection_id) ────────────────
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
             AND type = 'PK' AND name = 'PK_schema_config')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT PK_schema_config;
END
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_company')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT FK_schema_config_company;
END
GO

-- Only promote to NOT NULL if every row has a connection_id (i.e. backfill
-- found a match). If any are still null, the ALTER below fails and the
-- admin must investigate — don't silently continue.
IF NOT EXISTS (SELECT 1 FROM EMPOWER.RPT_schema_config WHERE connection_id IS NULL)
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config
        ALTER COLUMN connection_id UNIQUEIDENTIFIER NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID('EMPOWER.RPT_schema_config')
                 AND type = 'PK')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT PK_schema_config PRIMARY KEY (connection_id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_connection')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config
        ADD CONSTRAINT FK_schema_config_connection
        FOREIGN KEY (connection_id) REFERENCES EMPOWER.RPT_company_connections(id);
END
GO

-- company_id is now vestigial on RPT_schema_config. Left in place for this
-- migration; a follow-up can DROP COLUMN once we're confident the code is
-- clean of any reference.

-- ── 4. schema_config_history: same treatment ─────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history')
                 AND name = 'connection_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config_history
        ADD connection_id UNIQUEIDENTIFIER NULL;
END
GO

-- Backfill: history rows inherit the connection from their company's default.
UPDATE h
SET h.connection_id = c.id
FROM EMPOWER.RPT_schema_config_history h
CROSS APPLY (
    SELECT TOP 1 id
    FROM EMPOWER.RPT_company_connections
    WHERE company_id = h.company_id AND is_active = 1
    ORDER BY is_default DESC, name
) c
WHERE h.connection_id IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_schema_config_history_connection')
BEGIN
    ALTER TABLE EMPOWER.RPT_schema_config_history
        ADD CONSTRAINT FK_schema_config_history_connection
        FOREIGN KEY (connection_id) REFERENCES EMPOWER.RPT_company_connections(id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_schema_config_history_connection_updated'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_schema_config_history'))
BEGIN
    CREATE INDEX IX_schema_config_history_connection_updated
        ON EMPOWER.RPT_schema_config_history (connection_id, updated_at DESC);
END
GO
