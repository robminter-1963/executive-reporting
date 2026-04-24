-- ============================================================================
-- RPT_company_connections — per-company DB connection catalog
--
-- Adds a table to store one or more database connections per company.
-- Each company may have multiple named connections (e.g. 'primary',
-- 'warehouse') with exactly one marked as the default. Both SQL Server and
-- Postgres targets are supported via type-specific nullable columns.
--
-- Idempotent: safe to re-run. Paired with 2026-04-18_company_connections_rollback.sql.
--
-- ┌──────────────────────────────────────────────────────────────────────┐
-- │ SECURITY TODO — credential storage hardening (pre-production)       │
-- ├──────────────────────────────────────────────────────────────────────┤
-- │ Today the password and private-key columns store plaintext.         │
-- │ Before any non-TLE company is added that uses DB-stored creds,     │
-- │ implement ONE of:                                                   │
-- │                                                                      │
-- │  1. SQL Server Always Encrypted on ss_password, pg_password,        │
-- │     pg_ssl_key. Keys live in the Windows cert store or Azure KV.    │
-- │     DBAs with SELECT cannot read the values.                        │
-- │                                                                      │
-- │  2. Azure Key Vault references. Replace the plaintext columns with  │
-- │     a NVARCHAR secret-name column; app resolves at OpenAsync() time │
-- │     via IConnectionStringProvider.                                  │
-- │                                                                      │
-- │  3. App-level AES-GCM with a DPAPI- or KV-protected master key.     │
-- │     Simplest to implement, weakest of the three (master key compro- │
-- │     mise leaks everything).                                         │
-- │                                                                      │
-- │ Recommendation: option 2 for production. Option 3 acceptable for    │
-- │ early pilots if Key Vault isn't yet onboarded. Do NOT ship option 0 │
-- │ (plaintext) to prod.                                                │
-- └──────────────────────────────────────────────────────────────────────┘
-- ============================================================================

-- ── Ensure EMPOWER schema exists ─────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. Create the table ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_company_connections')
BEGIN
    CREATE TABLE EMPOWER.RPT_company_connections (
        id                          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        company_id                  UNIQUEIDENTIFIER NOT NULL
                                    REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
        name                        NVARCHAR(100)    NOT NULL,    -- e.g. 'primary', 'warehouse', 'replica'
        connection_type             NVARCHAR(20)     NOT NULL
                                    CHECK (connection_type IN ('sqlserver', 'postgres')),
        is_default                  BIT              NOT NULL DEFAULT 0,
        is_active                   BIT              NOT NULL DEFAULT 1,

        -- ── SQL Server fields ─────────────────────────────────────────
        ss_data_source              NVARCHAR(500)    NULL,
        ss_initial_catalog          NVARCHAR(200)    NULL,
        ss_integrated_security      BIT              NULL,
        ss_user_id                  NVARCHAR(200)    NULL,
        ss_password                 NVARCHAR(500)    NULL,        -- TODO: encrypt (see header block)
        ss_application_intent       NVARCHAR(20)     NULL,        -- 'ReadOnly' | 'ReadWrite' | NULL
        ss_encrypt                  BIT              NULL,
        ss_trust_server_certificate BIT              NULL,

        -- ── Postgres fields ───────────────────────────────────────────
        pg_host                     NVARCHAR(255)    NULL,
        pg_port                     INT              NULL,
        pg_database                 NVARCHAR(200)    NULL,
        pg_username                 NVARCHAR(200)    NULL,
        pg_password                 NVARCHAR(500)    NULL,        -- TODO: encrypt
        pg_ssl_mode                 NVARCHAR(20)     NULL,        -- 'Disable' | 'Prefer' | 'Require' | 'VerifyCA' | 'VerifyFull'
        pg_command_timeout          INT              NULL,        -- seconds, per-statement
        pg_timeout                  INT              NULL,        -- seconds, connection open
        pg_root_certificate         VARBINARY(MAX)   NULL,        -- CA chain (uploaded PEM/DER)
        pg_ssl_certificate          VARBINARY(MAX)   NULL,        -- client cert
        pg_ssl_key                  VARBINARY(MAX)   NULL,        -- TODO: encrypt — client private key

        created_at                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ── 2. Indexes ──────────────────────────────────────────────────────────
-- Fast lookup by company + default flag (hottest path).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_company_connections_company'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    CREATE INDEX IX_company_connections_company
        ON EMPOWER.RPT_company_connections (company_id, is_default DESC, is_active);
END
GO

-- At most one default per company — enforced via filtered unique index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_company_connections_default'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    CREATE UNIQUE INDEX UX_company_connections_default
        ON EMPOWER.RPT_company_connections (company_id)
        WHERE is_default = 1;
END
GO

-- Connection names must be unique within a company (so code can reference
-- '(company, name)' unambiguously).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_company_connections_name'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    CREATE UNIQUE INDEX UX_company_connections_name
        ON EMPOWER.RPT_company_connections (company_id, name);
END
GO

-- ── 3. Connection-type-specific field validation ─────────────────────────
-- Belt-and-suspenders check constraints: the type column gates which
-- field group may be populated. Prevents a 'postgres' row from having
-- ss_data_source accidentally set, and vice versa.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_company_connections_sqlserver_fields'
                 AND parent_object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD CONSTRAINT CK_company_connections_sqlserver_fields
        CHECK (
            connection_type <> 'sqlserver'
            OR (ss_data_source IS NOT NULL AND ss_initial_catalog IS NOT NULL)
        );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_company_connections_postgres_fields'
                 AND parent_object_id = OBJECT_ID('EMPOWER.RPT_company_connections'))
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD CONSTRAINT CK_company_connections_postgres_fields
        CHECK (
            connection_type <> 'postgres'
            OR (pg_host IS NOT NULL AND pg_database IS NOT NULL AND pg_username IS NOT NULL)
        );
END
GO

-- ── 4. Note on RPT_companies.connection_ref ─────────────────────────────
-- RPT_companies.connection_ref (the appsettings key) is RETAINED so the TLE
-- in-appsettings path continues to work. Lookup order in the
-- IDataSourceFactory will be:
--   1. If the company has rows in RPT_company_connections, use those
--      (prefer is_default = 1, else the first active row).
--   2. Otherwise fall back to RPT_companies.connection_ref via IConnectionStringProvider.
-- This lets us onboard new companies through the new table without
-- disturbing the bootstrap path.
