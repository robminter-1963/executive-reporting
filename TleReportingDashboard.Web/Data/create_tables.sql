-- ============================================================================
-- TLE Reporting Dashboard — Configuration Database Schema
--
-- Intended to run against the dedicated ConfigDb (e.g. TLE_ReportingConfig).
-- Every table lives under the EMPOWER schema regardless of the target DB.
-- The IF NOT EXISTS schema-create lets the same script run in either a fresh
-- ConfigDb or an existing EMPOWER DB without modification.
--
-- Current as of the multi-company refactor (Phase 1 + company_connections +
-- schema_config re-key). New companies can be added via RPT_companies +
-- RPT_company_connections rows; the app resolves each company's data-source
-- connection string dynamically at query time.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── companies ────────────────────────────────────────────────────────────
-- Registry of companies served by the application. Every user-scoped table
-- carries a company_id foreign key back to this table. The seeded TLE row
-- represents the initial single-company deployment.
CREATE TABLE EMPOWER.RPT_companies (
    id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    code              NVARCHAR(32)     NOT NULL UNIQUE,    -- URL segment: /c/{code}/...
    name              NVARCHAR(200)    NOT NULL,
    data_source_type  NVARCHAR(20)     NOT NULL,           -- 'sqlserver' | 'postgres'
    connection_ref    NVARCHAR(100)    NOT NULL,           -- legacy appsettings key; RPT_company_connections supersedes
    is_active         BIT              NOT NULL DEFAULT 1,
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ── user_companies ───────────────────────────────────────────────────────
-- Maps users to the companies they may access with a per-company permission.
-- Global admins are not required to appear here — their permission is global.
CREATE TABLE EMPOWER.RPT_user_companies (
    user_id     NVARCHAR(128)    NOT NULL,
    company_id  UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    permission  NVARCHAR(10)     NOT NULL CHECK (permission IN ('View', 'Edit')),
    is_default  BIT              NOT NULL DEFAULT 0,
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, company_id)
);
CREATE INDEX IX_user_companies_user_id ON EMPOWER.RPT_user_companies (user_id);
-- At most one default per user (filtered unique index)
CREATE UNIQUE INDEX UX_user_companies_default
    ON EMPOWER.RPT_user_companies (user_id)
    WHERE is_default = 1;

-- ── company_connections ──────────────────────────────────────────────────
-- Per-company DB connection catalog. Each company may have multiple named
-- connections (e.g. 'primary', 'warehouse') with exactly one is_default = 1.
-- SQL Server and Postgres targets are supported via type-specific columns.
--
-- SECURITY TODO: passwords and pg_ssl_key are currently plaintext. Before
-- any non-TLE company with DB-stored credentials goes to production,
-- implement Always Encrypted, Key Vault references, or app-level AES-GCM.
CREATE TABLE EMPOWER.RPT_company_connections (
    id                          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id                  UNIQUEIDENTIFIER NOT NULL
                                REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    name                        NVARCHAR(100)    NOT NULL,
    connection_type             NVARCHAR(20)     NOT NULL
                                CHECK (connection_type IN ('sqlserver', 'postgres')),
    is_default                  BIT              NOT NULL DEFAULT 0,
    is_active                   BIT              NOT NULL DEFAULT 1,

    -- SQL Server
    ss_data_source              NVARCHAR(500)    NULL,
    ss_initial_catalog          NVARCHAR(200)    NULL,
    ss_integrated_security      BIT              NULL,
    ss_user_id                  NVARCHAR(200)    NULL,
    ss_password                 NVARCHAR(500)    NULL,    -- TODO: encrypt
    ss_application_intent       NVARCHAR(20)     NULL,    -- 'ReadOnly' | 'ReadWrite' | NULL
    ss_encrypt                  BIT              NULL,
    ss_trust_server_certificate BIT              NULL,

    -- Postgres
    pg_host                     NVARCHAR(255)    NULL,
    pg_port                     INT              NULL,
    pg_database                 NVARCHAR(200)    NULL,
    pg_username                 NVARCHAR(200)    NULL,
    pg_password                 NVARCHAR(500)    NULL,    -- TODO: encrypt
    pg_ssl_mode                 NVARCHAR(20)     NULL,    -- 'Disable' | 'Prefer' | 'Require' | 'VerifyCA' | 'VerifyFull'
    pg_command_timeout          INT              NULL,
    pg_timeout                  INT              NULL,
    pg_root_certificate         VARBINARY(MAX)   NULL,
    pg_ssl_certificate          VARBINARY(MAX)   NULL,
    pg_ssl_key                  VARBINARY(MAX)   NULL,    -- TODO: encrypt

    -- Admin-authored WHERE-fragment appended to Schema Builder's table
    -- browser query. Dialect-specific, per-connection. Blank = all tables.
    table_filter_sql            NVARCHAR(MAX)    NULL,

    created_at                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT CK_company_connections_sqlserver_fields CHECK (
        connection_type <> 'sqlserver'
        OR (ss_data_source IS NOT NULL AND ss_initial_catalog IS NOT NULL)
    ),
    CONSTRAINT CK_company_connections_postgres_fields CHECK (
        connection_type <> 'postgres'
        OR (pg_host IS NOT NULL AND pg_database IS NOT NULL AND pg_username IS NOT NULL)
    )
);
CREATE INDEX IX_company_connections_company
    ON EMPOWER.RPT_company_connections (company_id, is_default DESC, is_active);
CREATE UNIQUE INDEX UX_company_connections_default
    ON EMPOWER.RPT_company_connections (company_id) WHERE is_default = 1;
CREATE UNIQUE INDEX UX_company_connections_name
    ON EMPOWER.RPT_company_connections (company_id, name);

-- ── admins ───────────────────────────────────────────────────────────────
-- Per-user admin assignments. Two scopes:
--   'global'  — admin across every company (bypasses per-company gates)
--   'company' — admin only for the referenced company
-- Global admin implies company admin for every company; the service layer
-- collapses the two checks.
-- Emails in appsettings Admins.Emails are auto-seeded as 'global' admins
-- on first service boot if they're not already present.
CREATE TABLE EMPOWER.RPT_admins (
    id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    email      NVARCHAR(256)    NOT NULL,                  -- Entra email (lookup key)
    user_id    NVARCHAR(128)    NULL,                      -- Entra object ID (filled on first sign-in)
    scope      NVARCHAR(20)     NOT NULL CHECK (scope IN ('global', 'company')),
    company_id UNIQUEIDENTIFIER NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    created_at DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by NVARCHAR(256)    NULL,
    CONSTRAINT CK_admins_company_when_scoped CHECK (
        (scope = 'global'  AND company_id IS NULL)
        OR (scope = 'company' AND company_id IS NOT NULL)
    )
);
CREATE UNIQUE INDEX UX_admins_global  ON EMPOWER.RPT_admins (email)             WHERE scope = 'global';
CREATE UNIQUE INDEX UX_admins_company ON EMPOWER.RPT_admins (email, company_id) WHERE scope = 'company';
CREATE INDEX IX_admins_email          ON EMPOWER.RPT_admins (email);
CREATE INDEX IX_admins_user_id        ON EMPOWER.RPT_admins (user_id);

-- ── saved_reports ────────────────────────────────────────────────────────
-- Stores report definitions: field selections, filters, dashboard configs.
-- ColumnState holds the dashboard view config (group by, measure, chart type,
-- custom labels, extra columns) as JSON.
CREATE TABLE EMPOWER.RPT_saved_reports (
    id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id        UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    name              NVARCHAR(250)    NOT NULL,
    owner_id          NVARCHAR(128)    NOT NULL,           -- Entra object ID (or 'SYSTEM' for templates)
    owner_email       NVARCHAR(256)    NOT NULL,
    field_ids         NVARCHAR(MAX)    NOT NULL,           -- JSON array
    filters           NVARCHAR(MAX)    NULL,               -- JSON object
    aggregations      NVARCHAR(MAX)    NULL,               -- JSON object
    column_state      NVARCHAR(MAX)    NULL,               -- JSON: dashboard config
    grid_template_id  UNIQUEIDENTIFIER NULL,               -- linked grid template
    connection_id     UNIQUEIDENTIFIER NULL                 -- FK → RPT_company_connections; immutable after first save
                      REFERENCES EMPOWER.RPT_company_connections(id),
    last_run_at       DATETIME2        NULL,
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_saved_reports_owner_id       ON EMPOWER.RPT_saved_reports (owner_id);
CREATE INDEX IX_saved_reports_company_owner  ON EMPOWER.RPT_saved_reports (company_id, owner_id);
CREATE INDEX IX_saved_reports_connection     ON EMPOWER.RPT_saved_reports (connection_id);

-- ── report_shares ────────────────────────────────────────────────────────
-- Grants access to a saved report for another user or security group.
CREATE TABLE EMPOWER.RPT_report_shares (
    id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id        UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    report_id         UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
    shared_with_id    NVARCHAR(128)    NOT NULL,           -- Entra object ID of user or group
    shared_with_type  NVARCHAR(10)     NOT NULL,           -- 'user' or 'group'
    permission        NVARCHAR(10)     NOT NULL DEFAULT 'viewer',  -- 'viewer' or 'editor'
    shared_by_id      NVARCHAR(128)    NOT NULL,           -- Entra object ID of the sharer
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_report_shares_report_id        ON EMPOWER.RPT_report_shares (report_id);
CREATE INDEX IX_report_shares_shared_with_id   ON EMPOWER.RPT_report_shares (shared_with_id);
CREATE INDEX IX_report_shares_company_report   ON EMPOWER.RPT_report_shares (company_id, report_id);

-- ── report_schedules ─────────────────────────────────────────────────────
-- Recurring email delivery schedules attached to saved reports.
-- Recipients follow the SSRS subscription model (free-form To/Cc/Bcc).
CREATE TABLE EMPOWER.RPT_report_schedules (
    id                   UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id           UNIQUEIDENTIFIER NOT NULL
                         REFERENCES EMPOWER.RPT_companies(id)
                         DEFAULT '00000000-0000-0000-0000-000000000001',
    report_id            UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
    owner_id             NVARCHAR(128)    NOT NULL,        -- Entra object ID
    owner_email          NVARCHAR(256)    NOT NULL,        -- Audit trail (always on TO)
    cron_expression      NVARCHAR(100)    NOT NULL,
    schedule_pattern     NVARCHAR(MAX)    NULL,            -- JSON; authoritative rich trigger
    start_date           DATETIME2        NULL,
    end_date             DATETIME2        NULL,
    subject              NVARCHAR(250)    NOT NULL,
    recipients           NVARCHAR(MAX)    NULL,            -- semicolon-separated TO
    cc_recipients        NVARCHAR(MAX)    NULL,            -- semicolon-separated CC
    bcc_recipients       NVARCHAR(MAX)    NULL,            -- semicolon-separated BCC
    attachment_format    NVARCHAR(10)     NOT NULL DEFAULT 'xlsx',   -- 'xlsx' or 'csv'
    include_preview      BIT              NOT NULL DEFAULT 1,
    is_active            BIT              NOT NULL DEFAULT 1,
    last_run_at          DATETIME2        NULL,
    last_run_status      NVARCHAR(50)     NULL,
    consecutive_failures INT              NOT NULL DEFAULT 0,
    hangfire_job_id      NVARCHAR(200)    NULL,
    created_at           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_report_schedules_report_id       ON EMPOWER.RPT_report_schedules (report_id);
CREATE INDEX IX_report_schedules_owner_id        ON EMPOWER.RPT_report_schedules (owner_id);
CREATE INDEX IX_report_schedules_company_owner   ON EMPOWER.RPT_report_schedules (company_id, owner_id, is_active);

-- ── user_preferences ─────────────────────────────────────────────────────
-- Per-user settings: onboarding state, default page sizes, master dashboard
-- title/logo, dark mode. PK is composite (user_id, company_id) so each user
-- can have different preferences per company they belong to.
CREATE TABLE EMPOWER.RPT_user_preferences (
    user_id                      NVARCHAR(128)    NOT NULL,    -- Entra object ID
    company_id                   UNIQUEIDENTIFIER NOT NULL
                                 REFERENCES EMPOWER.RPT_companies(id)
                                 DEFAULT '00000000-0000-0000-0000-000000000001',
    onboarding_completed         BIT              NOT NULL DEFAULT 0,
    default_page_size            INT              NOT NULL DEFAULT 100,
    report_library_page_size     INT              NOT NULL DEFAULT 15,
    report_page_sizes            NVARCHAR(MAX)    NULL,        -- JSON: {reportGuid: rowsPerPage}
    is_dark_mode                 BIT              NOT NULL DEFAULT 0,
    master_dashboard_title       NVARCHAR(200)    NULL,
    master_dashboard_title_align NVARCHAR(10)     NOT NULL DEFAULT 'left',   -- 'left' | 'center' | 'right'
    master_dashboard_logo        VARBINARY(MAX)   NULL,
    master_dashboard_logo_type   NVARCHAR(50)     NULL,
    created_at                   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_user_preferences PRIMARY KEY (user_id, company_id)
);

-- ── master_dashboard_tabs ────────────────────────────────────────────────
-- Per-user named tabs on the master dashboard.
CREATE TABLE EMPOWER.RPT_master_dashboard_tabs (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    company_id  UNIQUEIDENTIFIER NOT NULL
                REFERENCES EMPOWER.RPT_companies(id)
                DEFAULT '00000000-0000-0000-0000-000000000001',
    user_id     NVARCHAR(128)        NOT NULL,
    label       NVARCHAR(100)        NOT NULL DEFAULT 'Dashboard',
    sort_order  INT                  NOT NULL DEFAULT 0,
    title_align NVARCHAR(10)         NOT NULL DEFAULT 'left',   -- 'left' | 'center' | 'right'
    created_at  DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_master_tabs_user               ON EMPOWER.RPT_master_dashboard_tabs (user_id);
CREATE INDEX IX_master_dashboard_tabs_company_user ON EMPOWER.RPT_master_dashboard_tabs (company_id, user_id);

-- ── master_dashboard_sections ────────────────────────────────────────────
-- Optional sub-grouping under each tab. Tiles can either belong to a section
-- (section_id set on the tile) or render under a "(no section)" header for
-- the un-grouped bucket. Tab cascade-deletes its sections; tile.section_id
-- uses NO ACTION because the application clears references in
-- RemoveSectionAsync before deleting the row (avoids the SQL Server multi-
-- cascade-path warning when a tab delete could reach tiles via two paths).
CREATE TABLE EMPOWER.RPT_master_dashboard_sections (
    id          INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_master_dashboard_sections PRIMARY KEY,
    tab_id      INT             NOT NULL,
    label       NVARCHAR(100)   NOT NULL,
    sort_order  INT             NOT NULL,
    title_align VARCHAR(10)     NOT NULL CONSTRAINT DF_master_dashboard_sections_align     DEFAULT('left'),
    collapsed   BIT             NOT NULL CONSTRAINT DF_master_dashboard_sections_collapsed DEFAULT(0),
    CONSTRAINT FK_master_dashboard_sections_tab
        FOREIGN KEY (tab_id) REFERENCES EMPOWER.RPT_master_dashboard_tabs(id) ON DELETE CASCADE
);
CREATE INDEX IX_master_dashboard_sections_tab_sort ON EMPOWER.RPT_master_dashboard_sections (tab_id, sort_order);

-- ── master_dashboard_tiles ───────────────────────────────────────────────
-- Per-user master dashboard layout: which reports appear and in what order/size.
-- `source_company_id` is the company whose data the tile draws from (may differ
-- from the dashboard's owning company_id for cross-company dashboards).
-- `section_id` is nullable: NULL = render under the "(no section)" header.
CREATE TABLE EMPOWER.RPT_master_dashboard_tiles (
    id                INT IDENTITY(1,1) PRIMARY KEY,
    company_id        UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    source_company_id UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    user_id           NVARCHAR(128)        NOT NULL,
    tab_id            INT                  NOT NULL DEFAULT 0,
    section_id        INT                  NULL
                      CONSTRAINT FK_master_dashboard_tiles_section
                      REFERENCES EMPOWER.RPT_master_dashboard_sections(id),
    report_id         UNIQUEIDENTIFIER     NOT NULL,
    sort_order        INT                  NOT NULL DEFAULT 0,
    col_span          INT                  NOT NULL DEFAULT 12,
    height            INT                  NOT NULL DEFAULT 500,
    title_align       NVARCHAR(10)         NOT NULL DEFAULT 'left',   -- 'left' | 'center' | 'right'
    created_at        DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_master_tiles_user                     ON EMPOWER.RPT_master_dashboard_tiles (user_id);
CREATE INDEX IX_master_dashboard_tiles_company_tab    ON EMPOWER.RPT_master_dashboard_tiles (company_id, tab_id);
CREATE INDEX IX_master_dashboard_tiles_section        ON EMPOWER.RPT_master_dashboard_tiles (section_id);

-- ── schema_config ────────────────────────────────────────────────────────
-- Per-company JSON blob holding the full SchemaConfig (fields, joins, lookups,
-- custom filters, settings). Keyed by company_id so each company has an
-- independent schema. Replaces the legacy file-based schema_config.json.
CREATE TABLE EMPOWER.RPT_schema_config (
    company_id  UNIQUEIDENTIFIER NOT NULL
                REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    json        NVARCHAR(MAX)    NOT NULL,
    updated_by  NVARCHAR(256)    NULL,
    updated_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_schema_config PRIMARY KEY (company_id)
);

-- ── schema_config_history ────────────────────────────────────────────────
-- Audit trail of schema edits — one row per save, retained forever.
CREATE TABLE EMPOWER.RPT_schema_config_history (
    history_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
    company_id   UNIQUEIDENTIFIER NOT NULL
                 REFERENCES EMPOWER.RPT_companies(id)
                 DEFAULT '00000000-0000-0000-0000-000000000001',
    json         NVARCHAR(MAX)    NOT NULL,
    updated_by   NVARCHAR(256)    NULL,
    updated_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_schema_config_history_updated_at         ON EMPOWER.RPT_schema_config_history (updated_at DESC);
CREATE INDEX IX_schema_config_history_company_updated    ON EMPOWER.RPT_schema_config_history (company_id, updated_at DESC);

-- ── grid_templates ───────────────────────────────────────────────────────
-- Reusable grid configurations (field lists, column order, widths, visibility).
CREATE TABLE EMPOWER.RPT_grid_templates (
    id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    company_id      UNIQUEIDENTIFIER NOT NULL
                    REFERENCES EMPOWER.RPT_companies(id)
                    DEFAULT '00000000-0000-0000-0000-000000000001',
    name            NVARCHAR(200)    NOT NULL,
    description     NVARCHAR(500)    NULL,
    owner_id        NVARCHAR(128)    NOT NULL,
    owner_email     NVARCHAR(256)    NOT NULL DEFAULT '',
    is_shared       BIT              NOT NULL DEFAULT 0,
    field_ids       NVARCHAR(MAX)    NOT NULL DEFAULT '[]',
    column_state    NVARCHAR(MAX)    NULL,
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_grid_templates_owner           ON EMPOWER.RPT_grid_templates (owner_id);
CREATE INDEX IX_grid_templates_company_owner   ON EMPOWER.RPT_grid_templates (company_id, owner_id);

-- ============================================================================
-- Seed data
-- ============================================================================

-- ── Initial company (The Loan Exchange / TLE) ────────────────────────────
-- Fixed id so legacy user-scoped rows that predate multi-company awareness
-- still resolve to a valid company on upgrade. New code shouldn't rely on
-- this id being stable — the resolver picks the active is_default row at
-- runtime.
INSERT INTO EMPOWER.RPT_companies (id, code, name, data_source_type, connection_ref)
VALUES ('00000000-0000-0000-0000-000000000001', 'tle', 'The Loan Exchange', 'sqlserver', 'Database-TLE');

-- ── TLE primary data-source connection ───────────────────────────────────
-- Mirrors the legacy appsettings "Database-TLE" entry. Fill in ss_data_source
-- and ss_initial_catalog values appropriate to your environment (e.g.
-- 'tleempprod.loan.local' and 'EMPOWER' in prod).
INSERT INTO EMPOWER.RPT_company_connections (
    company_id, name, connection_type, is_default, is_active,
    ss_data_source, ss_initial_catalog, ss_integrated_security,
    ss_application_intent, ss_encrypt, ss_trust_server_certificate
)
VALUES (
    '00000000-0000-0000-0000-000000000001',   -- TLE
    'primary',
    'sqlserver',
    1, 1,
    'tleempprod.loan.local',                   -- TODO: adjust per env
    'EMPOWER',
    1,                                         -- integrated security
    'ReadOnly',
    1,                                         -- encrypt
    1                                          -- trust server cert
);

-- ============================================================================
-- JSON field formats reference:
--
-- field_ids:     ["loan_number","loan_amount","milestone"]
--
-- filters:       {
--                  "milestone": ["Funded","Applied"],     -- multi-select (IN clause)
--                  "loan_officer": "Sarah Johnson",       -- exact match
--                  "application_date_start": "2025-01-01",-- date range start
--                  "application_date_end": "2025-12-31"   -- date range end
--                }
--
-- column_state (dashboard config):
--                {
--                  "GroupByFieldId": "loan_officer",
--                  "MeasureFieldId": "loan_amount",
--                  "Aggregation": "SUM",
--                  "ChartType": "Bar",              -- "Bar","Pie","Line",""(none)
--                  "CustomLabels": {                 -- optional renamed headers
--                    "groupBy": "Officer Name",
--                    "measure": "Total Volume"
--                  },
--                  "ExtraColumns": [                 -- optional additional measures
--                    {"FieldId":"interest_rate","Aggregation":"AVG"},
--                    {"FieldId":"loan_number","Aggregation":"COUNT"}
--                  ]
--                }
--
-- aggregations:  {"loan_amount":"SUM","interest_rate":"AVG"}
-- ============================================================================
