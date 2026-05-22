-- ============================================================================
-- TLE Reporting Dashboard — Configuration Database Schema (CONSOLIDATED)
--
-- Intended to run against a dedicated ConfigDb (e.g. TLE_ReportingConfig).
-- Every table lives under the EMPOWER schema regardless of the target DB.
--
-- Folds in every migration applied since the Phase 1 multi-company baseline.
-- Tables are ordered by foreign-key dependency so a fresh DB can apply this
-- script top-to-bottom without forward-reference errors.
--
-- Migrations remain authoritative for upgrading existing databases — this
-- file produces the same end-state for a NEW database in one pass.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── companies ────────────────────────────────────────────────────────────
-- Registry of companies served by the application. Per-company connection
-- records live in RPT_company_connections. The legacy data_source_type /
-- connection_ref columns from Phase 1 were dropped in
-- 2026-05-09_drop_company_legacy_datasource.sql.
CREATE TABLE EMPOWER.RPT_companies (
    id                 UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    code               NVARCHAR(32)     NOT NULL UNIQUE,        -- URL segment: /c/{code}/...
    name               NVARCHAR(200)    NOT NULL,
    is_active          BIT              NOT NULL DEFAULT 1,
    -- Company-level branding (2026-04-23_company_logo, 2026-04-24_company_website_url).
    logo               VARBINARY(MAX)   NULL,
    logo_content_type  NVARCHAR(50)     NULL,
    website_url        NVARCHAR(500)    NULL,
    -- Display ordering on the company picker (2026-04-23_user_management_phase1).
    display_order      INT              NOT NULL DEFAULT 0,
    -- Hidden companies are excluded from end-user lists but kept in the DB
    -- for historical lookups (2026-05-05_company_hidden_flag).
    is_hidden          BIT              NOT NULL DEFAULT 0,
    -- Per-company toggle for the Master Dashboard KPI band
    -- (2026-05-20_company_kpis). 1 = visible when at least one KPI defined.
    show_kpi_band      BIT              NOT NULL DEFAULT 1,
    created_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ── admins ───────────────────────────────────────────────────────────────
-- Two scopes: 'global' (every company) or 'company' (one company).
-- appsettings Admins.Emails are auto-seeded as 'global' admins on first boot.
CREATE TABLE EMPOWER.RPT_admins (
    id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    email      NVARCHAR(256)    NOT NULL,                       -- Entra email (lookup key)
    user_id    NVARCHAR(128)    NULL,                           -- Entra oid (filled on first sign-in)
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

-- ── company_connections ──────────────────────────────────────────────────
-- Per-company DB connection catalog. SQL Server / Postgres / Dataverse
-- targets all coexist via type-specific column groups (only one group
-- populated per row, gated by CHECK constraints).
--
-- SECURITY TODO: ss_password, pg_password, pg_ssl_key, dv_client_secret
-- are plaintext. Encrypt before any non-ACME tenant goes to production
-- (Always Encrypted, Key Vault, or app-level AES-GCM).
CREATE TABLE EMPOWER.RPT_company_connections (
    id                              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id                      UNIQUEIDENTIFIER NOT NULL
                                    REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    name                            NVARCHAR(100)    NOT NULL,
    connection_type                 NVARCHAR(20)     NOT NULL
                                    CHECK (connection_type IN ('sqlserver', 'postgres', 'dataverse')),
    is_default                      BIT              NOT NULL DEFAULT 0,
    is_active                       BIT              NOT NULL DEFAULT 1,

    -- SQL Server
    ss_data_source                  NVARCHAR(500)    NULL,
    ss_initial_catalog              NVARCHAR(200)    NULL,
    ss_integrated_security          BIT              NULL,
    ss_user_id                      NVARCHAR(200)    NULL,
    ss_password                     NVARCHAR(500)    NULL,        -- TODO: encrypt
    ss_application_intent           NVARCHAR(20)     NULL,        -- 'ReadOnly' | 'ReadWrite' | NULL
    ss_encrypt                      BIT              NULL,
    ss_trust_server_certificate     BIT              NULL,
    ss_multiple_active_result_sets  BIT              NULL,        -- 2026-04-22_connection_mars

    -- Postgres
    pg_host                         NVARCHAR(255)    NULL,
    pg_port                         INT              NULL,
    pg_database                     NVARCHAR(200)    NULL,
    pg_username                     NVARCHAR(200)    NULL,
    pg_password                     NVARCHAR(500)    NULL,        -- TODO: encrypt
    pg_ssl_mode                     NVARCHAR(20)     NULL,
    pg_command_timeout              INT              NULL,
    pg_timeout                      INT              NULL,
    pg_root_certificate             VARBINARY(MAX)   NULL,
    pg_ssl_certificate              VARBINARY(MAX)   NULL,
    pg_ssl_key                      VARBINARY(MAX)   NULL,        -- TODO: encrypt
    pg_display_timezone             NVARCHAR(64)     NULL,        -- 2026-04-22_pg_display_timezone

    -- Dataverse (Microsoft Power Platform). Auth via Entra OAuth2 client_credentials.
    dv_environment_url              NVARCHAR(500)    NULL,
    dv_tenant_id                    NVARCHAR(100)    NULL,
    dv_client_id                    NVARCHAR(100)    NULL,
    dv_client_secret                NVARCHAR(500)    NULL,        -- TODO: encrypt

    -- Schema browser filters (2026-04-21_connection_schema_filter,
    -- 2026-04-18_connection_table_filter).
    schema_filter_sql               NVARCHAR(2000)   NULL,
    table_filter_sql                NVARCHAR(2000)   NULL,

    created_at                      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT CK_company_connections_sqlserver_fields CHECK (
        connection_type <> 'sqlserver'
        OR (ss_data_source IS NOT NULL AND ss_initial_catalog IS NOT NULL)
    ),
    CONSTRAINT CK_company_connections_postgres_fields CHECK (
        connection_type <> 'postgres'
        OR (pg_host IS NOT NULL AND pg_database IS NOT NULL AND pg_username IS NOT NULL)
    )
);
CREATE INDEX IX_company_connections_company ON EMPOWER.RPT_company_connections (company_id, is_default DESC, is_active);
CREATE UNIQUE INDEX UX_company_connections_default
    ON EMPOWER.RPT_company_connections (company_id) WHERE is_default = 1;
CREATE UNIQUE INDEX UX_company_connections_name
    ON EMPOWER.RPT_company_connections (company_id, name);

-- ── roles ────────────────────────────────────────────────────────────────
-- Catalog of job-function roles assignable on RPT_user_companies. Two
-- built-ins (Administrator, System Support) are always present; admin-
-- defined custom roles are stored alongside.
-- scope_rule drives row-level scoping:
--   'all'  — see everything (admins + System Support typically)
--   'self' — see only owned rows (loan officers, processors, etc.)
--   'team' — see own + team-managed rows
-- admin_sections is JSON array of admin-section keys the role can access
-- (System Support pattern — see RoleAdminSections in code).
CREATE TABLE EMPOWER.RPT_roles (
    id             UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    name           NVARCHAR(100)    NOT NULL UNIQUE,
    description    NVARCHAR(500)    NULL,
    is_active      BIT              NOT NULL DEFAULT 1,
    sort_order     INT              NOT NULL DEFAULT 0,
    scope_rule     NVARCHAR(32)     NOT NULL DEFAULT 'all'
                                    CHECK (scope_rule IN ('all', 'self', 'team')),
    admin_sections NVARCHAR(MAX)    NULL,                          -- JSON array
    created_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by     NVARCHAR(256)    NULL,
    updated_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ── users ────────────────────────────────────────────────────────────────
-- Cached user catalog. Pre-provisioned (admin invites by email before
-- first sign-in) and back-filled by Entra metadata on first login.
CREATE TABLE EMPOWER.RPT_users (
    email                    NVARCHAR(256)    NOT NULL PRIMARY KEY,
    user_id                  NVARCHAR(128)    NULL,                -- Entra oid, set on first sign-in
    display_name             NVARCHAR(256)    NULL,
    is_admin                 BIT              NOT NULL DEFAULT 0,  -- mirror of RPT_admins.global
    is_active                BIT              NOT NULL DEFAULT 1,
    role_id                  UNIQUEIDENTIFIER NULL REFERENCES EMPOWER.RPT_roles(id) ON DELETE SET NULL,
    last_visited_company_id  UNIQUEIDENTIFIER NULL REFERENCES EMPOWER.RPT_companies(id),
    prefers_company_picker   BIT              NOT NULL DEFAULT 0,  -- 2026-05-01_user_prefers_company_picker
    created_at               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by               NVARCHAR(256)    NULL,
    updated_at               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_users_user_id ON EMPOWER.RPT_users (user_id) WHERE user_id IS NOT NULL;

-- ── user_companies ───────────────────────────────────────────────────────
-- Maps users to the companies they may access with a per-company permission
-- and role. Global admins do not need a row here — their access is global.
-- email mirror added 2026-04-30 so legacy rows surface in invite flows.
CREATE TABLE EMPOWER.RPT_user_companies (
    user_id     NVARCHAR(128)    NOT NULL,
    company_id  UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    permission  NVARCHAR(10)     NOT NULL CHECK (permission IN ('View', 'Edit')),
    role        NVARCHAR(20)     NULL CHECK (role IN ('Editor', 'Viewer', 'Scheduler')),
    is_default  BIT              NOT NULL DEFAULT 0,
    email       NVARCHAR(256)    NULL,                              -- mirrors RPT_users.email
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, company_id)
);
CREATE INDEX IX_user_companies_user_id ON EMPOWER.RPT_user_companies (user_id);
CREATE UNIQUE INDEX UX_user_companies_default
    ON EMPOWER.RPT_user_companies (user_id) WHERE is_default = 1;

-- ── user_connection_logins ───────────────────────────────────────────────
-- Maps each user to their identity on each per-tenant data source. Used by
-- self-scoped role resolution: scope_rule='self' filters rows by
-- owner_field = external_user_id.
CREATE TABLE EMPOWER.RPT_user_connection_logins (
    user_id          NVARCHAR(128)    NOT NULL,
    connection_id    UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    external_user_id NVARCHAR(64)     NOT NULL,
    created_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, connection_id)
);
CREATE INDEX IX_user_connection_logins_user ON EMPOWER.RPT_user_connection_logins (user_id);

-- ── teams (+ members + user-team mapping + team-source SQL) ──────────────
-- Team-scoped roles (scope_rule='team') filter rows by membership of any
-- of the user's teams. RPT_teams + RPT_team_members are the imported team
-- roster; RPT_user_teams binds an app user to one or more teams; RPT_team_sources
-- holds the admin-authored "members SQL" used by the scheduled-report Worker.
CREATE TABLE EMPOWER.RPT_teams (
    connection_id   UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    team_id         INT              NOT NULL,
    team_name       NVARCHAR(100)    NULL,
    manager_ext_id  NVARCHAR(40)     NULL,
    manager_name    NVARCHAR(100)    NULL,
    team_type       NVARCHAR(20)     NULL,
    imported_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (connection_id, team_id)
);

CREATE TABLE EMPOWER.RPT_team_members (
    connection_id   UNIQUEIDENTIFIER NOT NULL,
    member_id       INT              NOT NULL,
    team_id         INT              NOT NULL,
    member_ext_id   NVARCHAR(40)     NULL,
    member_name     NVARCHAR(100)    NULL,
    imported_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (connection_id, member_id),
    CONSTRAINT FK_RPT_team_members_team FOREIGN KEY (connection_id, team_id)
        REFERENCES EMPOWER.RPT_teams (connection_id, team_id) ON DELETE CASCADE
);
CREATE INDEX IX_RPT_team_members_connection_login
    ON EMPOWER.RPT_team_members (connection_id, member_ext_id)
    INCLUDE (team_id, member_name);

CREATE TABLE EMPOWER.RPT_user_teams (
    user_id        NVARCHAR(128)    NOT NULL,
    connection_id  UNIQUEIDENTIFIER NOT NULL,
    team_id        INT              NOT NULL,
    created_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, connection_id, team_id),
    CONSTRAINT FK_RPT_user_teams_team FOREIGN KEY (connection_id, team_id)
        REFERENCES EMPOWER.RPT_teams (connection_id, team_id) ON DELETE CASCADE
);
CREATE INDEX IX_RPT_user_teams_user ON EMPOWER.RPT_user_teams (user_id);

-- Admin-curated team source SQL — emitted as a subquery by the team-scope
-- resolver. members_sql returns (team_id, member_ext_id) rows.
CREATE TABLE EMPOWER.RPT_team_sources (
    connection_id UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    team_type     NVARCHAR(20)     NOT NULL,
    members_sql   NVARCHAR(MAX)    NULL,        -- raw SELECT used at query time
    user_emails_sql NVARCHAR(MAX)  NULL,        -- raw SELECT for the user-emails import (2026-05-05)
    updated_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (connection_id, team_type)
);

-- Per-(connection, team_type) owner column used when emitting team
-- EXISTS predicates. e.g. ("LO_team", "PROCESSOR_USERID") so the team
-- scope filters on PROCESSOR_USERID for that team type.
CREATE TABLE EMPOWER.RPT_team_type_columns (
    connection_id UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    team_type     NVARCHAR(20)     NOT NULL,
    owner_column  NVARCHAR(128)    NOT NULL,
    updated_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (connection_id, team_type)
);

-- ── library_sections ─────────────────────────────────────────────────────
-- Admin-curated sub-categories for grouping reports in the Report Library.
-- Reports without a section land in the catch-all "(Uncategorized)" bucket
-- at render time. Soft-delete via is_active=0 keeps historical references.
CREATE TABLE EMPOWER.RPT_library_sections (
    id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RPT_library_sections PRIMARY KEY,
    company_id  UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    name        NVARCHAR(200)    NOT NULL,
    sort_order  INT              NOT NULL DEFAULT 0,
    is_active   BIT              NOT NULL DEFAULT 1,
    created_at  DATETIME         NOT NULL DEFAULT GETDATE()
);
CREATE INDEX IX_library_sections_company_sort
    ON EMPOWER.RPT_library_sections (company_id, sort_order, name);
CREATE UNIQUE INDEX UX_library_sections_company_name
    ON EMPOWER.RPT_library_sections (company_id, name) WHERE is_active = 1;

-- ── custom_primary_tables ────────────────────────────────────────────────
-- Per-connection curated list of root tables + aliases. Feeds the Report
-- Builder's Primary Table picker and the Schema Builder's Source dropdowns.
-- Aliases are required, lowercased, and unique per connection (enforced
-- in the service layer + DB indexes).
CREATE TABLE EMPOWER.RPT_custom_primary_tables (
    id                      UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    connection_id           UNIQUEIDENTIFIER NOT NULL
                            REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    table_name              NVARCHAR(200)    NOT NULL,
    alias                   NVARCHAR(60)     NOT NULL DEFAULT '',
    is_primary              BIT              NOT NULL DEFAULT 0,
    is_default_primary      BIT              NOT NULL DEFAULT 0,
    table_type              NVARCHAR(40)     NULL,
    primary_column          NVARCHAR(128)    NULL,
    additional_key_columns  NVARCHAR(500)    NULL,
    -- Optional free-text note shown in the Admin → Table Aliases list
    -- so admins remember what each alias is for (2026-05-21).
    description             NVARCHAR(500)    NULL,
    created_at              DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by_id           NVARCHAR(255)    NULL,
    created_by_email        NVARCHAR(255)    NULL
);
CREATE INDEX IX_custom_primary_tables_connection
    ON EMPOWER.RPT_custom_primary_tables (connection_id, table_name, alias);
CREATE UNIQUE INDEX UX_custom_primary_tables_unique
    ON EMPOWER.RPT_custom_primary_tables (connection_id, table_name, alias);
CREATE UNIQUE INDEX UX_custom_primary_tables_one_default
    ON EMPOWER.RPT_custom_primary_tables (connection_id) WHERE is_default_primary = 1;
CREATE UNIQUE INDEX UX_custom_primary_tables_alias
    ON EMPOWER.RPT_custom_primary_tables (connection_id, alias) WHERE alias <> '';

-- ── primary_table_role_owners ────────────────────────────────────────────
-- Per-(primary table, role) override of the owner column used for self-scoped
-- row filtering. Lets one table serve different roles where each role's
-- "ownership" lives in a different column (loan officer vs processor, etc.).
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

-- ── saved_reports ────────────────────────────────────────────────────────
-- Field selections, filters, dashboard configs. ColumnState is JSON.
-- internal_name is an optional admin-facing label distinct from the public
-- name (used by the dashboard's Add Report picker to disambiguate variants).
-- library_section_id buckets the report under an admin-curated section in
-- the Report Library.
CREATE TABLE EMPOWER.RPT_saved_reports (
    id                 UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id         UNIQUEIDENTIFIER NOT NULL
                       REFERENCES EMPOWER.RPT_companies(id)
                       DEFAULT '00000000-0000-0000-0000-000000000001',
    name               NVARCHAR(250)    NOT NULL,
    internal_name      NVARCHAR(200)    NULL,                      -- 2026-04-27
    category           NVARCHAR(64)     NULL,                      -- 2026-05-03
    owner_id           NVARCHAR(128)    NOT NULL,                  -- Entra oid
    owner_email        NVARCHAR(256)    NOT NULL,
    field_ids          NVARCHAR(MAX)    NOT NULL,                  -- JSON array
    filters            NVARCHAR(MAX)    NULL,                      -- JSON object
    aggregations       NVARCHAR(MAX)    NULL,                      -- JSON object
    column_state       NVARCHAR(MAX)    NULL,                      -- JSON: dashboard config
    grid_template_id   UNIQUEIDENTIFIER NULL,                      -- linked grid template (soft link)
    connection_id      UNIQUEIDENTIFIER NULL                       -- 2026-04-18; immutable after first save
                       REFERENCES EMPOWER.RPT_company_connections(id),
    primary_table      NVARCHAR(500)    NULL,                      -- 2026-04-18
    library_section_id UNIQUEIDENTIFIER NULL                       -- 2026-05-08
                       REFERENCES EMPOWER.RPT_library_sections(id) ON DELETE SET NULL,
    last_run_at        DATETIME2        NULL,
    created_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_saved_reports_owner_id          ON EMPOWER.RPT_saved_reports (owner_id);
CREATE INDEX IX_saved_reports_company_owner     ON EMPOWER.RPT_saved_reports (company_id, owner_id);
CREATE INDEX IX_saved_reports_connection        ON EMPOWER.RPT_saved_reports (connection_id);
CREATE INDEX IX_saved_reports_category          ON EMPOWER.RPT_saved_reports (category) WHERE category IS NOT NULL;
CREATE INDEX IX_saved_reports_library_section   ON EMPOWER.RPT_saved_reports (library_section_id) WHERE library_section_id IS NOT NULL;

-- ── report_shares ────────────────────────────────────────────────────────
CREATE TABLE EMPOWER.RPT_report_shares (
    id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id        UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    report_id         UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
    shared_with_id    NVARCHAR(128)    NOT NULL,                   -- Entra oid (user or group)
    shared_with_type  NVARCHAR(10)     NOT NULL,                   -- 'user' | 'group'
    permission        NVARCHAR(10)     NOT NULL DEFAULT 'viewer',  -- 'viewer' | 'editor'
    shared_by_id      NVARCHAR(128)    NOT NULL,
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_report_shares_report_id        ON EMPOWER.RPT_report_shares (report_id);
CREATE INDEX IX_report_shares_shared_with_id   ON EMPOWER.RPT_report_shares (shared_with_id);
CREATE INDEX IX_report_shares_company_report   ON EMPOWER.RPT_report_shares (company_id, report_id);

-- ── report_schedules ─────────────────────────────────────────────────────
-- Recurring email delivery + the team-fanout flavors added in 2026-05-05.
-- 'distribution' = single send to a fixed To/Cc/Bcc list.
-- 'individual'   = per-team-member fanout (one email per member, filtered to
--                  that member's owned rows). team_fanout controls the
--                  filtering ('members' | 'managers' | 'both').
CREATE TABLE EMPOWER.RPT_report_schedules (
    id                   UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    company_id           UNIQUEIDENTIFIER NOT NULL
                         REFERENCES EMPOWER.RPT_companies(id)
                         DEFAULT '00000000-0000-0000-0000-000000000001',
    report_id            UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
    owner_id             NVARCHAR(128)    NOT NULL,
    owner_email          NVARCHAR(256)    NOT NULL,
    cron_expression      NVARCHAR(100)    NOT NULL,
    schedule_pattern     NVARCHAR(MAX)    NULL,                    -- JSON; rich trigger (2026-04-16)
    start_date           DATETIME2        NULL,
    end_date             DATETIME2        NULL,
    subject              NVARCHAR(250)    NOT NULL,
    recipients           NVARCHAR(MAX)    NULL,
    cc_recipients        NVARCHAR(MAX)    NULL,
    bcc_recipients       NVARCHAR(MAX)    NULL,
    attachment_format    NVARCHAR(10)     NOT NULL DEFAULT 'xlsx', -- 'xlsx' | 'csv'
    include_preview      BIT              NOT NULL DEFAULT 1,
    is_active            BIT              NOT NULL DEFAULT 1,
    last_run_at          DATETIME2        NULL,
    last_run_status      NVARCHAR(500)    NULL,                    -- widened 2026-05-04
    consecutive_failures INT              NOT NULL DEFAULT 0,
    hangfire_job_id      NVARCHAR(200)    NULL,
    -- Team-fanout fields (2026-05-05_schedule_kind, 2026-05-09_team_fanout)
    kind                 NVARCHAR(20)     NOT NULL DEFAULT 'distribution',
    team_id              INT              NULL,
    team_connection_id   UNIQUEIDENTIFIER NULL,
    dist_email           NVARCHAR(255)    NULL,
    team_fanout          NVARCHAR(20)     NOT NULL DEFAULT 'members',
    created_at           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_report_schedules_report_id       ON EMPOWER.RPT_report_schedules (report_id);
CREATE INDEX IX_report_schedules_owner_id        ON EMPOWER.RPT_report_schedules (owner_id);
CREATE INDEX IX_report_schedules_company_owner   ON EMPOWER.RPT_report_schedules (company_id, owner_id, is_active);

-- ── user_preferences ─────────────────────────────────────────────────────
-- Per-(user, company) settings: page sizes, builder/library "last picked"
-- state, prefers_company_picker for users with multi-company access.
-- master_dashboard_* columns predate the per-company dashboard model and
-- are retained for legacy reads; new code reads logo/title from RPT_companies.
CREATE TABLE EMPOWER.RPT_user_preferences (
    user_id                       NVARCHAR(128)    NOT NULL,        -- Entra oid
    company_id                    UNIQUEIDENTIFIER NOT NULL
                                  REFERENCES EMPOWER.RPT_companies(id)
                                  DEFAULT '00000000-0000-0000-0000-000000000001',
    onboarding_completed          BIT              NOT NULL DEFAULT 0,
    default_page_size             INT              NOT NULL DEFAULT 100,
    report_library_page_size      INT              NOT NULL DEFAULT 15,
    report_page_sizes             NVARCHAR(MAX)    NULL,            -- JSON: {reportGuid: rowsPerPage}
    is_dark_mode                  BIT              NOT NULL DEFAULT 0,
    master_dashboard_title        NVARCHAR(200)    NULL,            -- legacy; per-user override
    master_dashboard_title_align  NVARCHAR(10)     NOT NULL DEFAULT 'left',
    master_dashboard_logo         VARBINARY(MAX)   NULL,
    master_dashboard_logo_type    NVARCHAR(50)     NULL,
    schema_builder_company_id     UNIQUEIDENTIFIER NULL,             -- 2026-04-21
    schema_builder_connection_id  UNIQUEIDENTIFIER NULL,             -- 2026-04-21
    report_library_company_id     UNIQUEIDENTIFIER NULL,             -- 2026-04-22
    last_master_dashboard_seen    DATETIME         NULL,             -- 2026-05-09; drives "what's new since"
    created_at                    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_user_preferences PRIMARY KEY (user_id, company_id)
);

-- ── master_dashboard_tabs ────────────────────────────────────────────────
-- Per-company shared tabs (2026-04-24 made dashboards shared per company;
-- user_id was dropped). Per-user visibility uses RPT_user_hidden_dashboard_tabs.
CREATE TABLE EMPOWER.RPT_master_dashboard_tabs (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    company_id  UNIQUEIDENTIFIER NOT NULL
                REFERENCES EMPOWER.RPT_companies(id)
                DEFAULT '00000000-0000-0000-0000-000000000001',
    label       NVARCHAR(100)    NOT NULL DEFAULT 'Dashboard',
    sort_order  INT              NOT NULL DEFAULT 0,
    title_align NVARCHAR(10)     NOT NULL DEFAULT 'left',
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_master_dashboard_tabs_company ON EMPOWER.RPT_master_dashboard_tabs (company_id, sort_order);

-- ── master_dashboard_sections ────────────────────────────────────────────
-- Optional sub-grouping under each tab. Tiles can belong to a section or
-- render under the "(no section)" header. tile.section_id uses NO ACTION
-- because the application clears references in RemoveSectionAsync before
-- deleting the row (avoids the multi-cascade-path error).
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
CREATE INDEX IX_master_dashboard_sections_tab_sort
    ON EMPOWER.RPT_master_dashboard_sections (tab_id, sort_order);

-- ── master_dashboard_tiles ───────────────────────────────────────────────
-- Per-company shared tiles. source_company_id is the company whose data the
-- tile draws from (may differ from company_id for cross-company dashboards).
-- section_id is nullable: NULL = render under "(no section)".
CREATE TABLE EMPOWER.RPT_master_dashboard_tiles (
    id                INT IDENTITY(1,1) PRIMARY KEY,
    company_id        UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    source_company_id UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_companies(id)
                      DEFAULT '00000000-0000-0000-0000-000000000001',
    tab_id            INT              NOT NULL DEFAULT 0,
    section_id        INT              NULL
                      CONSTRAINT FK_master_dashboard_tiles_section
                      REFERENCES EMPOWER.RPT_master_dashboard_sections(id),
    report_id         UNIQUEIDENTIFIER NOT NULL,
    sort_order        INT              NOT NULL DEFAULT 0,
    col_span          INT              NOT NULL DEFAULT 12,
    height            INT              NOT NULL DEFAULT 500,
    title_align       NVARCHAR(10)     NOT NULL DEFAULT 'left',
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_master_dashboard_tiles_company_tab_shared
    ON EMPOWER.RPT_master_dashboard_tiles (company_id, tab_id, sort_order);
CREATE INDEX IX_master_dashboard_tiles_section
    ON EMPOWER.RPT_master_dashboard_tiles (section_id);

-- ── user_hidden_dashboard_tabs ───────────────────────────────────────────
-- Per-user opt-out from individual tabs on the shared dashboard. Admins
-- curate the layout, users can hide tabs that don't apply to them.
CREATE TABLE EMPOWER.RPT_user_hidden_dashboard_tabs (
    user_id   NVARCHAR(255) NOT NULL,
    tab_id    INT           NOT NULL,
    hidden_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, tab_id),
    CONSTRAINT FK_user_hidden_dashboard_tabs_tab FOREIGN KEY (tab_id)
        REFERENCES EMPOWER.RPT_master_dashboard_tabs (id) ON DELETE CASCADE
);
CREATE INDEX IX_user_hidden_dashboard_tabs_tab_id
    ON EMPOWER.RPT_user_hidden_dashboard_tabs (tab_id);

-- ── schema_config ────────────────────────────────────────────────────────
-- Per-connection JSON blob holding the SchemaConfig (fields, joins, lookups,
-- custom filters, settings). Keyed by connection_id since 2026-04-18; the
-- legacy company_id-only key was migrated to per-connection.
CREATE TABLE EMPOWER.RPT_schema_config (
    connection_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
                  REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
    company_id    UNIQUEIDENTIFIER NOT NULL                            -- vestigial; kept for backfill queries
                  REFERENCES EMPOWER.RPT_companies(id)
                  DEFAULT '00000000-0000-0000-0000-000000000001',
    json          NVARCHAR(MAX)    NOT NULL,
    updated_by    NVARCHAR(256)    NULL,
    updated_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ── schema_config_history ────────────────────────────────────────────────
-- One row per Schema Builder save. Retained forever for the Schema History tab.
CREATE TABLE EMPOWER.RPT_schema_config_history (
    history_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
    connection_id UNIQUEIDENTIFIER NULL
                  REFERENCES EMPOWER.RPT_company_connections(id),
    company_id    UNIQUEIDENTIFIER NOT NULL
                  REFERENCES EMPOWER.RPT_companies(id)
                  DEFAULT '00000000-0000-0000-0000-000000000001',
    json          NVARCHAR(MAX)    NOT NULL,
    updated_by    NVARCHAR(256)    NULL,
    updated_at    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_schema_config_history_updated_at
    ON EMPOWER.RPT_schema_config_history (updated_at DESC);
CREATE INDEX IX_schema_config_history_company_updated
    ON EMPOWER.RPT_schema_config_history (company_id, updated_at DESC);

-- ── grid_templates ───────────────────────────────────────────────────────
-- Reusable grid configurations (field list, column order/widths/visibility).
-- Linked from saved_reports.grid_template_id; edits propagate on next render.
CREATE TABLE EMPOWER.RPT_grid_templates (
    id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    company_id      UNIQUEIDENTIFIER NOT NULL
                    REFERENCES EMPOWER.RPT_companies(id)
                    DEFAULT '00000000-0000-0000-0000-000000000001',
    connection_id   UNIQUEIDENTIFIER NULL                              -- 2026-04-20
                    REFERENCES EMPOWER.RPT_company_connections(id),
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
CREATE INDEX IX_grid_templates_owner          ON EMPOWER.RPT_grid_templates (owner_id);
CREATE INDEX IX_grid_templates_company_owner  ON EMPOWER.RPT_grid_templates (company_id, owner_id);

-- ── user_favorites ───────────────────────────────────────────────────────
-- Per-user pinned reports shown in the Master Dashboard's "Pinned" strip.
-- Cross-company (a user can pin reports from any company they can access).
CREATE TABLE EMPOWER.RPT_user_favorites (
    user_id     NVARCHAR(255)    NOT NULL,
    report_id   UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_saved_reports(id) ON DELETE CASCADE,
    sort_order  INT              NOT NULL DEFAULT 0,
    created_at  DATETIME         NOT NULL DEFAULT GETDATE(),
    PRIMARY KEY (user_id, report_id)
);
CREATE INDEX IX_user_favorites_user
    ON EMPOWER.RPT_user_favorites (user_id, sort_order, created_at);

-- ── user_notifications ───────────────────────────────────────────────────
-- In-app inbox for share/schedule/announcement events. Email-keyed so
-- pre-provisioned users accumulate a backlog before first sign-in.
CREATE TABLE EMPOWER.RPT_user_notifications (
    id                  UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    user_email          NVARCHAR(255)    NOT NULL,
    kind                NVARCHAR(64)     NOT NULL,
    title               NVARCHAR(200)    NOT NULL,
    body                NVARCHAR(1000)   NULL,
    link_url            NVARCHAR(500)    NULL,
    related_entity_type NVARCHAR(32)     NULL,
    related_entity_id   NVARCHAR(64)     NULL,
    is_read             BIT              NOT NULL DEFAULT 0,
    created_at          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_user_notifications_user_unread
    ON EMPOWER.RPT_user_notifications (user_email, is_read, created_at DESC);

-- ── app_theme ────────────────────────────────────────────────────────────
-- Theme palette stored as JSON. company_id IS NULL is the global default;
-- non-null rows override per-company. The Admin → Theme tab edits these.
CREATE TABLE EMPOWER.RPT_app_theme (
    id          INT              NOT NULL PRIMARY KEY,
    json        NVARCHAR(MAX)    NOT NULL,
    company_id  UNIQUEIDENTIFIER NULL,                              -- 2026-05-09; per-company override
    updated_by  NVARCHAR(255)    NULL,
    updated_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_app_theme_company
    ON EMPOWER.RPT_app_theme (company_id) WHERE company_id IS NOT NULL;

-- ── app_settings ─────────────────────────────────────────────────────────
-- Free-form key/value store for admin-configurable settings (column-width
-- defaults, feature toggles, etc.). Centralizes one-off knobs that aren't
-- worth a dedicated table.
CREATE TABLE EMPOWER.RPT_app_settings (
    [key]      NVARCHAR(100) NOT NULL PRIMARY KEY,
    [value]    NVARCHAR(MAX) NULL,
    updated_at DATETIME      NOT NULL DEFAULT GETDATE(),
    updated_by NVARCHAR(256) NULL
);

-- ── company_kpis ─────────────────────────────────────────────────────────
-- Per-company KPI cards rendered in the Master Dashboard's KPI band above
-- the tab strip. Filtering supports an optional period (mtd/qtd/ytd/last_30d/
-- last_90d/custom + compare-previous), schema custom filters, and ad-hoc
-- value filters layered AND with the period.
CREATE TABLE EMPOWER.RPT_company_kpis (
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RPT_company_kpis PRIMARY KEY,
    company_id        UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    -- NO ACTION (not CASCADE) on the connection FK because RPT_company_connections
    -- itself cascades from RPT_companies, and SQL Server refuses two cascade paths
    -- into the same row. Orphaned KPIs (connection deleted) render an em-dash.
    connection_id     UNIQUEIDENTIFIER NOT NULL
                      REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE NO ACTION,
    primary_table     NVARCHAR(200)    NOT NULL,
    label             NVARCHAR(120)    NULL,
    field_id          NVARCHAR(120)    NOT NULL,
    aggregation       NVARCHAR(10)     NOT NULL CONSTRAINT DF_RPT_company_kpis_agg DEFAULT 'sum',
    date_field_id     NVARCHAR(120)    NULL,
    period            NVARCHAR(20)     NULL,
    compare_previous  BIT              NOT NULL CONSTRAINT DF_RPT_company_kpis_compare DEFAULT 0,
    col_span          INT              NOT NULL CONSTRAINT DF_RPT_company_kpis_colspan DEFAULT 3,
    sort_order        INT              NOT NULL CONSTRAINT DF_RPT_company_kpis_sort DEFAULT 0,
    -- Phase 1.1 filter columns (2026-05-20_company_kpis_filters):
    filters           NVARCHAR(MAX)    NULL,                       -- JSON {fieldId: value}
    custom_filter_ids NVARCHAR(MAX)    NULL,                       -- JSON [string]
    date_from         DATETIME2        NULL,
    date_to           DATETIME2        NULL,
    created_at        DATETIME         NOT NULL CONSTRAINT DF_RPT_company_kpis_created DEFAULT GETDATE(),
    created_by_email  NVARCHAR(256)    NULL,
    CONSTRAINT CK_RPT_company_kpis_agg
        CHECK (aggregation IN ('sum','count','avg','min','max')),
    CONSTRAINT CK_RPT_company_kpis_period
        CHECK (period IS NULL OR period IN ('mtd','qtd','ytd','last_30d','last_90d','custom'))
);
CREATE INDEX IX_company_kpis_company_sort
    ON EMPOWER.RPT_company_kpis (company_id, sort_order);

-- ============================================================================
-- Seed data
-- ============================================================================

-- ── Initial company (ACME) ───────────────────────────────────────────────
-- Fixed id so legacy user-scoped rows that predate multi-company awareness
-- still resolve to a valid company on upgrade.
INSERT INTO EMPOWER.RPT_companies (id, code, name)
VALUES ('00000000-0000-0000-0000-000000000001', 'acme', 'ACME');

-- ── Initial primary data-source connection ───────────────────────────────
-- Mirrors the legacy appsettings entry. Fill in ss_data_source +
-- ss_initial_catalog values appropriate to your environment.
INSERT INTO EMPOWER.RPT_company_connections (
    company_id, name, connection_type, is_default, is_active,
    ss_data_source, ss_initial_catalog, ss_integrated_security,
    ss_application_intent, ss_encrypt, ss_trust_server_certificate
)
VALUES (
    '00000000-0000-0000-0000-000000000001',
    'primary',
    'sqlserver',
    1, 1,
    'localhost',                              -- TODO: adjust per env
    'ACME',                                   -- TODO: adjust per env
    1,                                        -- integrated security
    'ReadOnly',
    1,                                        -- encrypt
    1                                         -- trust server cert
);

-- ── Default theme (global, light palette) ────────────────────────────────
-- id=1 is the singleton global row; company-scoped overrides go in
-- separate rows with company_id populated.
INSERT INTO EMPOWER.RPT_app_theme (id, json, company_id, updated_by)
VALUES (1, N'{"mode":"light","primary":"#4F46E5","accent":"#10B981"}', NULL, 'seed');

-- ── Built-in roles ──────────────────────────────────────────────────────
-- Administrator + System Support are reserved. Both have scope_rule='all'.
-- admin_sections JSON list governs which Admin tabs each role can open.
INSERT INTO EMPOWER.RPT_roles (name, description, sort_order, scope_rule, admin_sections)
VALUES
    ('Administrator',  'Full access to every admin section. Sees every active company.', 0, 'all',
        N'["companies","db_connections","users","roles","team_builder","schedules","schema_history","schema_builder","promotion","theme","app_settings","column_widths"]'),
    ('System Support', 'Cross-company visibility with configurable admin-section access.', 1, 'all',
        N'["companies","db_connections","users","schedules"]');

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
--                  "ChartType": "Bar",
--                  "CustomLabels": { "groupBy": "Officer Name", "measure": "Total Volume" },
--                  "ExtraColumns": [
--                    {"FieldId":"interest_rate","Aggregation":"AVG"},
--                    {"FieldId":"loan_number","Aggregation":"COUNT"}
--                  ],
--                  "DetailGroupByFieldId": "state",
--                  "DetailGroupByDirection": "asc",
--                  "DetailSortFieldId": "loan_amount",
--                  "DetailSortDirection": "desc",
--                  "TableColumnOrder": ["loan_number","loan_amount","status"],
--                  "ColumnWidths": {"loan_number": 120, "loan_amount": 160},
--                  "HiddenColumns": ["internal_notes"],
--                  "CustomFilterIds": ["active_only"],
--                  "TableCalculatedColumns": [{"Key":"yield","Label":"Yield","Formula":"[interest_rate] * [loan_amount]"}],
--                  "TableSort": [{"Field":"loan_amount","Direction":"desc"}],
--                  "Distinct": true
--                }
--
-- aggregations:  {"loan_amount":"SUM","interest_rate":"AVG"}
--
-- kpi.filters:        {"status": "Funded", "loan_type": "Conventional"}
-- kpi.custom_filter_ids: ["active_only","current_year"]
--
-- role.admin_sections: ["companies","db_connections","users","schedules"]
-- ============================================================================
