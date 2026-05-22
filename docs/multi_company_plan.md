---
title: "Multi-Company Architecture Plan"
subtitle: "Executive Reporting Suite"
author: "Engineering"
date: "2026-04-17"
---

# 1. Executive Summary

The Executive Reporting Suite today serves a single Empower database. This document describes how to evolve it to support **multiple companies**, each with its own data source (SQL Server, Postgres, or any ADO.NET-compatible DBMS), shared application instance, and a single sign-on experience. It also describes the preparatory work required so connection strings can later move from `appsettings.json` to a secure secrets store (Azure Key Vault or equivalent) with no code changes.

**Key decisions already made:**

- Each company has its own `schema_config` (fields, joins, lookups, custom filters).
- Users may belong to multiple companies. For any company other than their primary edit company, access is **view-only**. Global administrators bypass this restriction.
- Only a single global administrator role exists; there is no per-company admin role.
- Company scoping is expressed in the URL as a path segment: `/c/{company-code}/...`.
- **No cross-company aggregation.** A single dashboard may host tiles drawn from different companies, but each tile queries exactly one company's data source. Data blending, if ever required, is a future concern.
- Connection strings remain in `appsettings.json` for the initial rollout. All code is written against an abstraction that lets us swap the provider for Azure Key Vault (or any other secrets store) via a one-line DI change when policy requires it.

**Phases at a glance:**

| Phase | Scope | User-visible change |
|-------|-------|-----------------------|
| −1 | `IConnectionStringProvider` abstraction | None |
| 0  | `ISqlDialect` + `DbConnection` refactor | None |
| 1  | Data-model changes (tables, columns) | None |
| 2  | Company context + URL routing + data-source factory | Company switcher; URLs gain `/c/{code}/` |
| 3  | Onboard second company (Postgres) | Admin can configure and use Company B |
| 4  | View/Edit permission split; per-tile company binding | Non-admins see view-only UI where applicable |

---

# 2. Goals and Assumptions

## 2.1 Goals

1. **Data isolation.** A user entitled only to Company A must never be able to read raw rows from Company B, directly or through crafted input.
2. **Heterogeneous sources.** A company's data may live in SQL Server, Postgres, or any ADO.NET-compatible store. SalesForce data is assumed to be reverse-ETL'd into Postgres; live SOQL is out of scope.
3. **Single application instance.** One IIS site, one auth provider. Isolation is enforced at the data-source and authorization layers, not by running separate app processes.
4. **Per-company schemas.** Each company's field catalog, joins, and custom filters are independent. An edit to Company A's schema has no effect on Company B.
5. **Shared single sign-on.** One Entra ID tenant; user identity is global across companies.
6. **Operational flexibility for secrets.** Connection strings live in `appsettings.json` today and can move to Azure Key Vault without code changes tomorrow.

## 2.2 Non-goals

- Cross-company query aggregation (UNION ALL across companies in a single query).
- Live querying against the SalesForce API.
- Per-company administrator roles.
- Per-user customization of `schema_config` (schemas remain admin-curated).

---

# 3. Architecture Overview

## 3.1 Layers

The refactor adds three horizontal abstractions. None exists today.

1. **`IConnectionStringProvider`** (Phase −1) — the single point of truth for where connection strings come from. Today it reads `appsettings.json`; tomorrow it can read from Key Vault with no changes to consumers.
2. **`ISqlDialect`** (Phase 0) — a small interface encapsulating the SQL differences between sources: paging syntax, identifier quoting, parameter prefix, current-timestamp expression.
3. **`IDataSourceFactory` + `ICompanyContext`** (Phase 2) — given a company id, returns an opened `DbConnection` of the correct provider type and the matching `ISqlDialect`. `ICompanyContext` is a per-circuit scoped service that resolves the current company from the URL and exposes it to every downstream service.

## 3.2 Component interaction

```
Blazor circuit / HTTP request
        │
        ├─▶ CompanyAuthorizationMiddleware (resolves /c/{code}, checks RPT_user_companies)
        │     │
        │     └─▶ ICompanyContext (scoped) { CompanyId, Permission }
        │
        ├─▶ SchemaService  ──▶ ISchemaConfigStore.GetAsync(CompanyId)
        │
        └─▶ QueryService
              ├─▶ IDataSourceFactory.OpenAsync(CompanyId) → DbConnection
              ├─▶ IDataSourceFactory.GetDialect(CompanyId) → ISqlDialect
              └─▶ QueryBuilder.BuildQuery(request, fields, joins, filters, lookups, dialect)
```

## 3.3 What stays unchanged

- `QueryBuilder`'s core logic — projection, filter emission, JOIN dependency sorting, parameter collection — remains the same. It simply routes paging, identifier quoting, and TOP-style limits through `ISqlDialect`.
- `ScheduleCron`, `FieldReferenceService`, `JoinLabelFormatter`, `SchemaConfigStore` (reshaped to be company-scoped but otherwise identical).
- The Entra ID authentication layer, the global admin allowlist, the snackbar and dialog patterns.

---

# 4. Data Model Changes

All changes are additive in the sense that Phase 1 can ship without changing any runtime behavior. No existing column is dropped or retyped.

## 4.1 New tables

### `RPT_companies`

Registry of companies served by the application. The seeded `DEFAULT` row represents the current single-company deployment.

```sql
CREATE TABLE EMPOWER.RPT_companies (
    id                   UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    code                 NVARCHAR(32)     NOT NULL UNIQUE,
    name                 NVARCHAR(200)    NOT NULL,
    data_source_type     NVARCHAR(20)     NOT NULL,     -- 'sqlserver' | 'postgres' | ...
    connection_ref       NVARCHAR(100)    NOT NULL,     -- key into IConnectionStringProvider
    is_active            BIT              NOT NULL DEFAULT 1,
    created_at           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### `RPT_user_companies`

Assigns users to companies with a view/edit permission. Administrators are not required to appear in this table — their permission is global.

```sql
CREATE TABLE EMPOWER.RPT_user_companies (
    user_id              NVARCHAR(128)    NOT NULL,
    company_id           UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    permission           NVARCHAR(10)     NOT NULL CHECK (permission IN ('View', 'Edit')),
    is_default           BIT              NOT NULL DEFAULT 0,
    created_at           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, company_id)
);

CREATE INDEX IX_user_companies_user_id ON EMPOWER.RPT_user_companies (user_id);
```

Invariant: a user may have at most one `is_default = 1` row. Enforced by a filtered unique index:

```sql
CREATE UNIQUE INDEX UX_user_companies_default
    ON EMPOWER.RPT_user_companies (user_id)
    WHERE is_default = 1;
```

## 4.2 Column additions to existing tables

Add `company_id UNIQUEIDENTIFIER NOT NULL` (with FK to `RPT_companies.id`) to every user-scoped table. Existing indexes are extended to lead with `company_id`.

| Table                                | New column   | New/updated index                         |
|---------------------------------------|--------------|-------------------------------------------|
| `RPT_saved_reports`                   | `company_id` | `(company_id, owner_id, is_template)`     |
| `RPT_report_shares`                   | `company_id` | `(company_id, report_id)`                 |
| `RPT_report_schedules`                | `company_id` | `(company_id, owner_id, is_active)`       |
| `RPT_grid_templates`                  | `company_id` | `(company_id, owner_id)`                  |
| `RPT_master_dashboard_tabs`           | `company_id` | `(company_id, owner_id)`                  |
| `RPT_master_dashboard_tiles`          | `company_id` | `(company_id, tab_id)`                    |
| `RPT_user_preferences`                | `company_id` | `(user_id, company_id)` composite PK      |
| `RPT_schema_config`                   | `company_id` | `(company_id)` as PK (drop singleton check) |
| `RPT_schema_config_history`           | `company_id` | `(company_id, updated_at DESC)`           |

**Dashboard tiles** additionally receive a `source_company_id UNIQUEIDENTIFIER NOT NULL` column (distinct from the owning dashboard's `company_id`). This is what makes a tile "belong to" a specific company's data source — a user viewing their default dashboard can host tiles sourced from any company they have View permission on.

## 4.3 Schema config singleton constraint

The `CK_schema_config_singleton` check constraint (introduced when the schema moved into the DB) must be dropped before adding `company_id`. The new primary key becomes `(company_id)`:

```sql
ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT CK_schema_config_singleton;
ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT PK__RPT_schema_config;  -- name varies; look it up
ALTER TABLE EMPOWER.RPT_schema_config ADD company_id UNIQUEIDENTIFIER NOT NULL
    CONSTRAINT DF_schema_config_company DEFAULT '<DEFAULT company GUID>';
ALTER TABLE EMPOWER.RPT_schema_config ADD CONSTRAINT PK_schema_config PRIMARY KEY (company_id);
ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT DF_schema_config_company;
```

The default is used only for the backfill; drop it afterward so every new row must supply its company id explicitly.

## 4.4 Backfill strategy

Phase 1's migration runs in three logical steps per table:

1. Add the column as `NULL` with a default of the seeded `DEFAULT` company id.
2. Run `UPDATE ... SET company_id = '<DEFAULT>'` for any rows (backfill).
3. Set the column to `NOT NULL` and drop the default.

All user-owned rows land in the `DEFAULT` company. No runtime queries or reads break because `ICompanyContext` always resolves to `DEFAULT` until Phase 2 activates real routing.

---

# 5. Code Changes by Subsystem

## 5.1 `IConnectionStringProvider` (Phase −1)

```csharp
public interface IConnectionStringProvider
{
    string Get(string key);         // throws InvalidOperationException if missing
    string? TryGet(string key);     // returns null if missing
}
```

**Initial implementation** reads from `IConfiguration.GetSection("ConnectionStrings")`. A `CompositeConnectionStringProvider` is registered later to try multiple sources (Key Vault → appsettings).

Every service that today calls `configuration.GetConnectionString("X")` is refactored to take `IConnectionStringProvider` instead. Affected services: `ReportDbService`, `SchemaConfigStore`, `FieldReferenceService`, and the future `DataSourceFactory`.

`IConfiguration` remains the source of truth for non-secret settings (admin allowlist, Serilog, AzureAd configuration, etc.).

## 5.2 `ISqlDialect` (Phase 0)

```csharp
public interface ISqlDialect
{
    string Name { get; }                                          // "sqlserver" | "postgres"
    string QuoteIdentifier(string name);                          // [x] vs "x"
    string ParameterPrefix { get; }                               // "@" for both via Npgsql
    string CurrentTimestampExpression { get; }                    // GETDATE() vs NOW()
    string FormatPaging(int offset, int limit);                   // OFFSET/FETCH vs LIMIT/OFFSET
    string FormatTop(int n);                                      // "TOP n " vs ""
    // FormatTop returns "" for dialects that use LIMIT; callers append "LIMIT n" via FormatPaging.
}
```

Two implementations ship initially:

- `SqlServerDialect` — behavior identical to today's QueryBuilder.
- `PostgresDialect` — `"x"` identifier quoting, `LIMIT N OFFSET M` paging, `NOW()` timestamp.

`QueryBuilder.BuildQuery` gains an `ISqlDialect dialect` parameter. All uses of `TOP N`, `FOR JSON`, or `SELECT TOP 500` must route through the dialect. The per-field `SqlExpression`, `SqlJoin`, `SqlPreamble` values remain opaque raw SQL owned by the per-company schema — the admin's responsibility, not the builder's.

## 5.3 `IDataSourceFactory` and `ICompanyContext` (Phase 2)

```csharp
public interface IDataSourceFactory
{
    Task<DbConnection> OpenAsync(Guid companyId, CancellationToken ct = default);
    ISqlDialect GetDialect(Guid companyId);
    IReadOnlyList<Company> GetAllCompanies();
}

public interface ICompanyContext
{
    Guid CompanyId { get; }
    string CompanyCode { get; }
    Permission Permission { get; }    // View | Edit (Edit implies View)
    bool IsGlobalAdmin { get; }
}
```

`DataSourceFactory` reads `RPT_companies` at startup into a cache. `OpenAsync` looks up `connection_ref`, obtains the string via `IConnectionStringProvider`, and instantiates the appropriate `DbConnection`:

- `data_source_type = 'sqlserver'` → `new SqlConnection(...)`
- `data_source_type = 'postgres'` → `new NpgsqlConnection(...)` (requires `Npgsql` package)

`GetDialect` returns the cached dialect singleton for that type.

## 5.4 `CompanyAuthorizationMiddleware`

Runs immediately after authentication. Responsibilities:

1. Parse the `/c/{code}/...` prefix from the request path. Rewrite the request path to the segment after the prefix so existing routes still match.
2. Look up `code` in `RPT_companies`.
3. Check `RPT_user_companies` for `(user_id, company_id)` — load the user's `permission`. If the user is a global admin and no row exists, synthesize `Permission = Edit`.
4. Populate the scoped `ICompanyContext`.
5. If the user isn't entitled to the company and isn't a global admin, return `404` (not `403`). A `403` leaks the company's existence.

Requests without a `/c/{code}` prefix redirect to `/c/{default-code}/...` where `default-code` is the user's `is_default = 1` row (or the first company they have access to if no default is marked).

## 5.5 Repository layer

Every repository method gains a `Guid companyId` parameter (or equivalent `ICompanyContext` dependency). Every query adds `AND company_id = @c`.

**Write-path guard:** Every repository write operation (save, update, delete, schedule, share) verifies `companyContext.Permission == Permission.Edit || companyContext.IsGlobalAdmin` before touching the database. A `PermissionDeniedException` is thrown if the check fails. The UI layer hides write controls proactively, but the repository is the authoritative gate.

**Read-path:** No permission check beyond entitlement to the company itself. If a user has View permission to Company A, they can see all reports/templates/schedules/shares in Company A (subject to existing sharing model).

## 5.6 Scheduled-report worker

The worker's `ScheduledReportJob` currently resolves a single Empower connection at startup. It must be refactored to:

1. Read `company_id` from each schedule row.
2. Resolve the company's `DbConnection` via `IDataSourceFactory`.
3. Resolve the company's `schema_config` via `ISchemaConfigStore`.
4. Use the company's `ISqlDialect` when building the query.

No worker-side permission checks are needed — the schedule was saved under a user who had Edit permission at the time; subsequent permission revocation is reflected on next Edit, not retroactively on existing schedules. (A follow-up improvement could revalidate permissions each time a schedule fires; left as future work.)

## 5.7 UI changes

- **Company switcher** in the AppBar — dropdown of `GetCompaniesForUser()`. Selecting a company writes to `UserPreferences.LastCompanyCode` and navigates to `/c/{new-code}/reports`.
- **URL rewriting** across every `NavigationManager.NavigateTo` call site. A helper `NavigateWithinCompany(string relativePath)` avoids duplicated string concatenation.
- **Breadcrumb** gains the company name as the first segment.
- **Permission-aware controls.** On Report Library, Report Builder, Grid Templates, Master Dashboard: if `Permission == View` and `!IsGlobalAdmin`, hide Save / Edit / Delete / Schedule / Share icons. Keep Clone available (clone becomes a report in the user's Edit-permission default company).
- **Share links** include `/c/{code}/` so recipients land on the correct company even if their own default differs.

---

# 6. Phase-by-Phase Rollout

Each phase is independently shippable and reversible. No phase enables customer-visible multi-company behavior until Phase 3.

## Phase −1 — Connection-string abstraction

| Item | Detail |
|------|--------|
| Duration | ~1 day |
| Risk | None (no behavioral change) |
| Rollback | Revert PR |

Deliverables:

1. `IConnectionStringProvider` interface.
2. `AppSettingsConnectionStringProvider` reading from `IConfiguration`.
3. DI registration in `Program.cs`.
4. All existing `configuration.GetConnectionString(...)` call sites refactored.
5. Startup log emits `"Resolved 'X' from appsettings"` at Debug level so future Key Vault flips are observable.

No migration SQL. No user-visible change.

## Phase 0 — SQL dialect abstraction and `DbConnection` refactor

| Item | Detail |
|------|--------|
| Duration | ~2 days |
| Risk | Low — existing behavior re-routed through new interfaces |
| Rollback | Revert PR |

Deliverables:

1. `ISqlDialect` interface.
2. `SqlServerDialect` implementation.
3. `QueryBuilder.BuildQuery` signature gains `ISqlDialect`. `SqlServerDialect` passed everywhere.
4. Services swap `SqlConnection` for `DbConnection`. `SqlClient` continues to provide the concrete type.
5. All 35 existing tests continue to pass unmodified.

No migration SQL. No user-visible change.

## Phase 1 — Data-model changes

| Item | Detail |
|------|--------|
| Duration | ~2 days (migration + code) |
| Risk | Medium — touches every user-scoped table |
| Rollback | Scripted rollback migration provided |

### 1.1 Migration script

```sql
-- File: Data/migrations/2026-05-01_multi_company_schema.sql

-- Companies registry
CREATE TABLE EMPOWER.RPT_companies (
    id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    code             NVARCHAR(32)     NOT NULL UNIQUE,
    name             NVARCHAR(200)    NOT NULL,
    data_source_type NVARCHAR(20)     NOT NULL,
    connection_ref   NVARCHAR(100)    NOT NULL,
    is_active        BIT              NOT NULL DEFAULT 1,
    created_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

DECLARE @DefaultCompanyId UNIQUEIDENTIFIER = NEWID();

INSERT INTO EMPOWER.RPT_companies (id, code, name, data_source_type, connection_ref)
VALUES (@DefaultCompanyId, 'default', 'Default Company', 'sqlserver', 'EmpowerReplica');

-- User-company mapping
CREATE TABLE EMPOWER.RPT_user_companies (
    user_id      NVARCHAR(128)    NOT NULL,
    company_id   UNIQUEIDENTIFIER NOT NULL REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
    permission   NVARCHAR(10)     NOT NULL CHECK (permission IN ('View', 'Edit')),
    is_default   BIT              NOT NULL DEFAULT 0,
    created_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, company_id)
);
CREATE INDEX IX_user_companies_user_id ON EMPOWER.RPT_user_companies (user_id);
CREATE UNIQUE INDEX UX_user_companies_default
    ON EMPOWER.RPT_user_companies (user_id)
    WHERE is_default = 1;

-- Backfill: every existing user is assigned Edit permission on DEFAULT
INSERT INTO EMPOWER.RPT_user_companies (user_id, company_id, permission, is_default)
SELECT DISTINCT owner_id, @DefaultCompanyId, 'Edit', 1
FROM EMPOWER.RPT_saved_reports;

-- Add company_id to every user-scoped table
DECLARE @t NVARCHAR(100);
DECLARE cur CURSOR FOR
    SELECT t FROM (VALUES
        ('RPT_saved_reports'), ('RPT_report_shares'), ('RPT_report_schedules'),
        ('RPT_grid_templates'), ('RPT_master_dashboard_tabs'),
        ('RPT_master_dashboard_tiles'), ('RPT_user_preferences')
    ) v(t);
OPEN cur;
FETCH NEXT FROM cur INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @sql NVARCHAR(MAX) =
        'ALTER TABLE EMPOWER.' + @t + ' ADD company_id UNIQUEIDENTIFIER NULL;';
    EXEC sp_executesql @sql;
    SET @sql = 'UPDATE EMPOWER.' + @t + ' SET company_id = @c WHERE company_id IS NULL;';
    EXEC sp_executesql @sql, N'@c UNIQUEIDENTIFIER', @c = @DefaultCompanyId;
    SET @sql = 'ALTER TABLE EMPOWER.' + @t + ' ALTER COLUMN company_id UNIQUEIDENTIFIER NOT NULL;';
    EXEC sp_executesql @sql;
    SET @sql = 'ALTER TABLE EMPOWER.' + @t + ' ADD CONSTRAINT FK_' + @t +
               '_company FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);';
    EXEC sp_executesql @sql;
    FETCH NEXT FROM cur INTO @t;
END
CLOSE cur;
DEALLOCATE cur;

-- Schema config: drop singleton, re-key on company_id
ALTER TABLE EMPOWER.RPT_schema_config DROP CONSTRAINT CK_schema_config_singleton;
-- (Look up and drop the existing PK by name, then:)
ALTER TABLE EMPOWER.RPT_schema_config ADD company_id UNIQUEIDENTIFIER NULL;
UPDATE EMPOWER.RPT_schema_config SET company_id = @DefaultCompanyId WHERE id = 1;
ALTER TABLE EMPOWER.RPT_schema_config DROP COLUMN id;
ALTER TABLE EMPOWER.RPT_schema_config ALTER COLUMN company_id UNIQUEIDENTIFIER NOT NULL;
ALTER TABLE EMPOWER.RPT_schema_config ADD CONSTRAINT PK_schema_config PRIMARY KEY (company_id);
ALTER TABLE EMPOWER.RPT_schema_config ADD CONSTRAINT FK_schema_config_company
    FOREIGN KEY (company_id) REFERENCES EMPOWER.RPT_companies(id);

-- Dashboard tiles: source_company_id (what company's data this tile shows)
ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
    ADD source_company_id UNIQUEIDENTIFIER NULL;
UPDATE EMPOWER.RPT_master_dashboard_tiles
    SET source_company_id = company_id WHERE source_company_id IS NULL;
ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
    ALTER COLUMN source_company_id UNIQUEIDENTIFIER NOT NULL;
ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
    ADD CONSTRAINT FK_tiles_source_company FOREIGN KEY (source_company_id)
        REFERENCES EMPOWER.RPT_companies(id);

-- Extended indexes
CREATE INDEX IX_saved_reports_company_owner
    ON EMPOWER.RPT_saved_reports (company_id, owner_id, is_template);
CREATE INDEX IX_grid_templates_company_owner
    ON EMPOWER.RPT_grid_templates (company_id, owner_id);
CREATE INDEX IX_report_schedules_company_owner
    ON EMPOWER.RPT_report_schedules (company_id, owner_id, is_active);
-- ... etc for other tables
```

### 1.2 Code changes in Phase 1

- No behavioral change. Code still hard-codes `CompanyContext.CompanyId = <DEFAULT id>`. Every repository method signature gains `companyId` but the controller/page layer always passes the DEFAULT value.
- Hard-coding is removed in Phase 2.

### 1.3 Rollback

```sql
-- Drop FKs
ALTER TABLE EMPOWER.RPT_saved_reports DROP CONSTRAINT FK_RPT_saved_reports_company;
-- (repeat for each table)

-- Drop columns
ALTER TABLE EMPOWER.RPT_saved_reports DROP COLUMN company_id;
-- (repeat)

-- Drop new tables
DROP TABLE EMPOWER.RPT_user_companies;
DROP TABLE EMPOWER.RPT_companies;
```

## Phase 2 — Company context and routing

| Item | Detail |
|------|--------|
| Duration | ~3 days |
| Risk | Medium — every service call site changes |
| Rollback | Revert PR (DB is forward-compatible) |

Deliverables:

1. `ICompanyContext`, `IDataSourceFactory`, `CompanyAuthorizationMiddleware`.
2. URL rewriting: all routes match on `/c/{code}/...`. Legacy routes redirect to the user's default company.
3. Every repository takes company scope from context rather than a hardcoded DEFAULT.
4. Company switcher in AppBar (single option — DEFAULT — until Phase 3, but functional).
5. `LastCompanyCode` added to `UserPreferences`.
6. Integration tests: a user without a `RPT_user_companies` row receives 404 for any `/c/{code}` URL.

User-visible change: URLs gain `/c/default/` prefix. No functional change.

## Phase 3 — Onboarding the second company

| Item | Detail |
|------|--------|
| Duration | ~2 days (excluding schema authoring) |
| Risk | Low if Phase 2 is solid |

Deliverables:

1. `Npgsql` package dependency and `PostgresDialect` implementation.
2. `DataSourceFactory` gains support for `data_source_type = 'postgres'`.
3. New connection-string key in `appsettings.json` (e.g. `Empower.BETA`).
4. New row in `RPT_companies` for Company B.
5. Blank `RPT_schema_config` row seeded for Company B; admin authors fields, joins, custom filters in the Schema Builder (now with a company picker, admin-only).
6. Pilot user accounts receive `RPT_user_companies` entries assigning View on DEFAULT, Edit on BETA (or vice versa, depending on pilot).

User-visible change: the company switcher shows two options. Each company's reports/dashboards are isolated.

## Phase 4 — Permission split and per-tile company binding

| Item | Detail |
|------|--------|
| Duration | ~2 days |
| Risk | Low |

Deliverables:

1. UI honors `Permission = View`: Save / Edit / Delete / Schedule / Share buttons hidden. Global admins always see them.
2. Repository `PermissionDeniedException` raised if a Write call reaches the repo without Edit permission — defense in depth behind the UI gating.
3. Master Dashboard tile editor: `source_company_id` picker (admin-only; selects from the user's accessible companies).
4. Tile rendering: each tile resolves its own `DbConnection` and `schema_config` via its `source_company_id`.
5. Share-dialog scope: sharing a report shares it within the owning company only.

User-visible change: users with View-only access on a company see a read-only experience. Admins see an unrestricted experience across all companies. Master Dashboards may host tiles from different companies.

---

# 7. Operational Considerations

## 7.1 Connection-string rotation

With `IConnectionStringProvider` in place, secret rotation reduces to editing `appsettings.json` or (future) rotating the secret in Key Vault. No deployment is needed. Services do not cache resolved strings internally; every `OpenAsync` call queries the provider.

## 7.2 Startup diagnostics

The existing startup log that prints a resolved connection-string summary (server + database, no credentials) is extended to iterate every row in `RPT_companies`. A missing or invalid connection string fails startup with a clear error identifying the offending company code.

## 7.3 Onboarding a new company (runbook)

1. Add the connection string to `appsettings.json` (or Key Vault) under a new key, e.g. `Empower.NEWCO`.
2. Insert a row into `RPT_companies`: `code = 'newco'`, `data_source_type = 'sqlserver' | 'postgres'`, `connection_ref = 'Empower.NEWCO'`.
3. Sign in as an admin; open Schema Builder; select the new company from the company picker; author fields and joins.
4. Assign users via an admin UI (new, part of Phase 3) or direct `INSERT INTO RPT_user_companies`.
5. Verify the company appears in the switcher for those users.

## 7.4 Removing a company

1. Set `is_active = 0` on the `RPT_companies` row. The switcher hides it; routing returns 404.
2. Historical data in user-scoped tables is retained under the inactive company id. A separate purge script can be run later if full deletion is required.

## 7.5 Global admin behavior

- An admin sees every company in the switcher regardless of `RPT_user_companies` entries.
- An admin has Edit permission everywhere.
- The admin allowlist (`appsettings.json → Admins.Emails`) is unchanged. No migration.
- A future enhancement — admin can impersonate a non-admin user's company view for debugging — is out of scope.

---

# 8. Testing Strategy

- **Unit tests** for `ISqlDialect` implementations (paging, identifier quoting, parameter prefix).
- **Unit tests** for `IDataSourceFactory`: mocks `IConnectionStringProvider` and asserts the correct `DbConnection` subtype is returned.
- **Middleware tests** for `CompanyAuthorizationMiddleware`: user with no entitlement → 404; user with entitlement → `ICompanyContext` populated; admin bypass.
- **Integration tests** (in-memory mock) for each repository verifying `company_id` isolation: a reader with Company A context cannot see Company B rows even when they attempt direct ID lookup.
- **Regression suite**: every existing test continues to pass under the single `DEFAULT` company. Phases −1, 0, and 1 should ship without changing any assertion.

---

# 9. Open Questions to Resolve Before Phase 0

1. **Does SQL parameter naming in Npgsql match what the codebase uses today?** (`@name` style is supported; `$N` is not required.) Confirm via a small prototype query.
2. **Are Schema Builder's introspection queries dialect-portable?** They currently hit `INFORMATION_SCHEMA.COLUMNS`; Postgres's `information_schema` has the same shape but some column differences (for example, `character_maximum_length` exists in both). If introspection should work for Postgres companies, add a dialect-specific provider; if not, disable the Browse Tables UI for non-SQL-Server companies and require admins to type identifiers manually. Recommendation: disable for now, revisit on demand.
3. **Dashboard tile editor — company selector placement.** Inline on each tile vs a default for the whole dashboard? Recommendation: inline per tile, default inherits from the dashboard's `company_id`.
4. **Share-link company encoding.** Include `/c/{code}/` in URLs even for single-company users (future-proofing) or only when multi-company is active? Recommendation: always include.
5. **Global admin visibility of other companies' shared reports.** Admins see all by definition. Does this need an audit trail? Recommendation: log admin reads of cross-company data at Information level.

Answering these before Phase 0 ships unlocks the rest of the plan with minimal rework.

---

# Appendix A — File and Namespace Layout

New files introduced across all phases:

```
Configuration/
    Company.cs                             (model)
    CompanyPermission.cs                   (enum: View | Edit)

Services/
    IConnectionStringProvider.cs           (Phase -1)
    AppSettingsConnectionStringProvider.cs (Phase -1)
    ISqlDialect.cs                         (Phase 0)
    SqlServerDialect.cs                    (Phase 0)
    PostgresDialect.cs                     (Phase 3)
    IDataSourceFactory.cs                  (Phase 2)
    DataSourceFactory.cs                   (Phase 2)
    ICompanyContext.cs                     (Phase 2)
    CompanyContext.cs                      (Phase 2; scoped)
    ICompanyRegistry.cs                    (Phase 2)
    CompanyRegistry.cs                     (Phase 2; singleton, cache)

Middleware/
    CompanyAuthorizationMiddleware.cs      (Phase 2)

Data/migrations/
    2026-05-01_multi_company_schema.sql    (Phase 1)
    2026-05-15_multi_company_rollback.sql  (Phase 1, kept for emergency)
```

# Appendix B — Risk Register

| Risk | Mitigation |
|------|-----------|
| Admin accidentally assigns Edit on the wrong company | Admin UI (Phase 3) shows a preview of the user's effective permissions before save. |
| Postgres schema drift surprises the admin | Schema Builder hides the "Browse Tables" feature for non-SQL-Server companies in Phase 3; manual entry only. |
| User sees tiles that fail to load because a source company's DB is down | Tile shows an inline error message, other tiles remain functional. |
| Connection string for a new company added to `appsettings.json` but never read | Startup validation enumerates `RPT_companies` and fails fast if any `connection_ref` is unresolved. |
| Global admin accidentally edits the wrong company's schema | Schema Builder header always displays the currently selected company code in a distinct color. A confirmation dialog on Save lists the company for which changes are being persisted. |
| Rotating a secret breaks an active query | `IConnectionStringProvider` is queried on each `OpenAsync`; existing open connections are unaffected and close normally. |

---
