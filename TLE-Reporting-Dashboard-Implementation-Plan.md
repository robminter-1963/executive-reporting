# TLE Reporting Dashboard — Implementation Plan

**Date:** April 6, 2026
**Status:** Approved for Development (Revised for SOX/GLBA Compliance)
**Replaces:** Power BI ($20/user/month PPU) + SSRS ticket requests

---

## Executive Summary

Build a self-service reporting dashboard that lets TLE business users create reports from Empower LOS data without involving IT. One application, one deployment, no per-seat licensing. Users land on a report library showing their saved, shared, and template reports. From there they build reports by selecting fields (with search and descriptions), apply filters including custom date ranges, view KPIs and charts, save/share reports with role-aware redaction, and export to Excel or CSV — all from a browser.

To comply with SOX and GLBA requirements, all data-mapping logic (fields and joins) is managed via source control (Configuration as Code), and automated report deliveries are strictly locked to the authenticated user's internal Entra ID email address. The system enforces dynamic "runtime redaction" to ensure users can collaborate on shared reports without violating data clearance bounds.

---

## Tech Stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| Framework | .NET 10 Blazor Web App (Server) | Single language (C#), real-time via SignalR, direct DB access, no separate API layer. Circuit idle timeout and max-circuit limits are tuned per environment (see Capacity & Resilience). |
| UI Library | MudBlazor 9.x | Production-grade component library — data grids, charts, dialogs, theming |
| Data Access | Entity Framework Core + raw ADO.NET | EF Core for user state (`saved_reports`, schedules); raw ADO.NET against Empower replica |
| Configuration | `IOptionsSnapshot<T>` | `schema_config.json` provides whitelist definitions for fields and JOINs. Validated at startup (see Schema Config Validation). |
| Logging | Serilog + Azure Monitor | Immutable, centralized structured logging for SOX/GLBA audit trails (`Serilog.Sinks.AzureAnalytics`) |
| Auth | Microsoft.Identity.Web (Entra ID SSO) | Guarded by environment — stub handler only activates in `Development`. Hard-fail startup in Production if Entra ID is unconfigured. |
| Charts | MudChart (Bar/Line/Pie) | SVG-based, no JavaScript chart dependencies |
| Job Scheduling | Hangfire 1.8.x (AspNetCore + SqlServer) | Persistent background jobs with SQL Server storage and built-in retry. **Runs in a dedicated worker project**, isolated from the Blazor web process (see Solution Structure). |
| Email Delivery | Microsoft Graph SDK (app-only) | Send scheduled report emails via a shared service mailbox with strict templating |
| Orchestration | .NET Aspire 13.2 | Health checks, OpenTelemetry observability, service discovery, local dev dashboard |
| Deploy | Azure App Service (×2) | Two deployment targets via Aspire: one for the Blazor web app, one for the Hangfire worker. Both deployed through the standard CI/CD pipeline. |

---

## Solution Structure

```text
TleReportingDashboard.sln
├── TleReportingDashboard.AppHost/         # Aspire orchestrator
│   └── Program.cs                         # Resource definitions: web app, worker, SQL connections
├── TleReportingDashboard.ServiceDefaults/ # Shared cross-cutting concerns
│   └── Extensions.cs                      # OpenTelemetry, health checks, service discovery
├── TleReportingDashboard.Core/            # Shared models, interfaces, config types
│   ├── Models/                            # Data contracts (shared by Web + Worker)
│   ├── Configuration/                     # Strongly-typed schema_config.json models
│   └── Interfaces/                        # Service interfaces (IQueryPipeline, IEmailService, etc.)
├── TleReportingDashboard.Web/             # Blazor dashboard application
│   ├── Program.cs                         # DI, middleware, service wiring, startup guards
│   ├── schema_config.json                 # Core system configuration (Fields, JOINs, Dates)
│   ├── Data/                              # EF Core DbContexts (User state only)
│   ├── Services/                          # Business logic
│   │   ├── SchemaService.cs               # Reads/filters schema_config.json based on Entra roles
│   │   ├── SchemaConfigValidator.cs       # Startup validation of all SQL fragments in schema_config.json
│   │   ├── QueryPipeline/                 # Decomposed query engine (see Query Pipeline Architecture)
│   │   │   ├── FieldResolver.cs           # Validates field IDs → resolves SourceTable + SourceColumn
│   │   │   ├── JoinResolver.cs            # Determines required JOINs from selected fields
│   │   │   ├── RedactionEnforcer.cs       # Swaps restricted columns with DefaultRedactionValue per RBAC
│   │   │   ├── AggregationBuilder.cs      # GROUP BY generation, aggregate functions, redacted-measure coalescing
│   │   │   ├── DateFilterTranslator.cs    # Translates relative date tokens to validated SQL functions
│   │   │   ├── QueryGuardrails.cs         # Enforces max row limits, command timeouts, pagination
│   │   │   └── SqlEmitter.cs              # Final parameterized SQL assembly + ADO.NET execution
│   │   ├── ExportService.cs               # Multi-format export: Excel (.xlsx via ClosedXML), CSV
│   │   ├── SharingService.cs              # Report sharing CRUD, permission model, "shared with me" queries
│   │   ├── ScheduleService.cs             # CRUD for report_schedules + Hangfire registration
│   │   └── EmailService.cs                # Microsoft Graph send with forced headers/footers
│   ├── Components/                        # Blazor UI 
│   │   ├── Pages/
│   │   │   ├── ReportLibrary.razor        # Landing page: My Reports, Shared With Me, Templates, Recent
│   │   │   └── ReportBuilder.razor        # Report builder: field picker, filters, grid, charts, export
│   │   └── Shared/
│   │       ├── FieldPicker.razor          # Searchable, categorized field selector with descriptions
│   │       ├── DateRangeFilter.razor      # Relative presets + custom from/to date picker
│   │       ├── ReportGrid.razor           # MudDataGrid wrapper: sort, reorder, resize, hide columns
│   │       ├── ShareDialog.razor          # MudDialog: share report with users/teams, manage permissions
│   │       ├── ExportMenu.razor           # Export dropdown: Excel, CSV
│   │       ├── ScheduleDialog.razor       # MudDialog: create/edit a schedule (locked to user email)
│   │       └── ScheduleManager.razor      # List + manage schedules for a saved report
│   └── wwwroot/                           # CSS + print stylesheet
├── TleReportingDashboard.Worker/          # Dedicated Hangfire job processor (separate App Service)
│   ├── Program.cs                         # Hangfire server bootstrap, DI, service wiring
│   └── Jobs/
│       └── ScheduledReportJob.cs          # Hangfire job: query → format → email via Graph
├── TleReportingDashboard.Tests/           # Unit + component tests (xUnit, bUnit, Moq)
│   ├── Pipeline/                          # Per-stage isolation tests for QueryPipeline
│   ├── Services/                          # SchemaService, ScheduleService, EmailService tests
│   ├── Validation/                        # SchemaConfigValidator, startup guard tests
│   └── Components/                        # bUnit tests for Razor components
├── TleReportingDashboard.Tests.E2E/       # End-to-end browser tests (Playwright)
│   ├── playwright.config.ts               # Base URL, browser matrix, timeout, CI settings
│   ├── Fixtures/
│   │   └── AppFixture.cs                  # Spins up Aspire AppHost in dev mode for test runs
│   ├── Tests/
│   │   ├── ReportLibraryTests.cs         # Landing page loads, tabs work, recent/saved/shared/templates render
│   │   ├── FieldPickerTests.cs           # Search filters fields, descriptions render, domain groups work
│   │   ├── QueryFlowTests.cs             # Field select → run → grid renders with data
│   │   ├── GridInteractionTests.cs       # Column sort, reorder, resize, hide/show
│   │   ├── DateFilterTests.cs            # Relative presets work, custom date range from/to validated
│   │   ├── RedactionFlowTests.cs         # Unauthorized user sees REDACTED in DOM, banner visible
│   │   ├── SharingFlowTests.cs           # Share dialog → add user → shared report appears in recipient's library
│   │   ├── ScheduleFlowTests.cs          # Create schedule → email field is read-only, locked to user
│   │   ├── ExportTests.cs                # Excel/CSV download triggers, files contain expected content
│   │   ├── PaginationTests.cs            # Large result set → pagination controls present, no full load
│   │   ├── GuardrailBannerTests.cs       # Truncation warning banner appears at max row limit
│   │   ├── PrintTests.cs                 # Print stylesheet applied, grid renders in print-friendly layout
│   │   ├── OnboardingTests.cs            # First-run walkthrough renders for new user, dismisses correctly
│   │   ├── ResilienceTests.cs            # Replica down → error banner renders, no unhandled crash
│   │   └── AccessibilityTests.cs         # axe-core scan on ReportLibrary, ReportBuilder, ShareDialog
│   └── Helpers/
│       └── StubClaimsOverride.cs          # Helpers to toggle role claims between test scenarios
````

---

## Architecture

Plaintext

```
Browser ←── SignalR ──→ Blazor Server (.NET 10)
                           │
                           ├── SchemaService (Reads schema_config.json via IOptionsSnapshot)
                           │       └── SchemaConfigValidator (startup-time SQL fragment validation)
                           │
                           ├── QueryPipeline
                           │       ├── FieldResolver → JoinResolver → RedactionEnforcer
                           │       ├── → AggregationBuilder → DateFilterTranslator
                           │       ├── → QueryGuardrails (max rows, command timeout)
                           │       ├── → SqlEmitter (parameterized SQL + ADO.NET)
                           │       └── ADO.NET → Empower Read-Only Replica
                           │
                           ├── ReportService
                           │       └── EF Core → TLE_ReportingConfig DB (saved_reports)
                           │
                           └── ScheduleService
                                   └── EF Core → TLE_ReportingConfig DB (report_schedules)
                                           └── Hangfire API → registers/removes recurring jobs


TleReportingDashboard.Worker (Separate App Service)
                           │
                           └── Hangfire Server (dedicated process)
                                   └── ScheduledReportJob (on cron trigger)
                                           ├── Verifies Entra ID user is still active
                                           ├── QueryPipeline → Empower Replica (run report)
                                           ├── Formats results as HTML table + CSV attachment
                                           └── EmailService → Microsoft Graph API → Sender's Entra Email
                                                   └── Resilience: Polly retry + circuit breaker on Graph API
```

---

## Security & Compliance Architecture

### SOX Compliance (IT General Controls)

- **Configuration as Code:** There is no "Admin UI" to alter database mappings. All fields, data types, and `JOIN` clauses are defined in `schema_config.json`.
- **Change Management:** Adding a new reportable field requires an Azure DevOps task, a pull request, peer review, and deployment via the standard CI/CD pipeline. This prevents unauthorized alteration of financial reporting logic.
- **Immutable Audit Logging:** Every data query is logged to Azure Monitor (via Serilog). Logs include: `EventType`, `UserObjectId`, `UserEmail`, `FieldsRequested`, `FiltersApplied`, `RowCount`, and `ExecutionMs`.

### GLBA Compliance (Data Privacy)

- **No NPI Exfiltration via Email:** Users cannot send automated reports to arbitrary external or internal email addresses. The `ScheduleDialog` does not have a recipient input. Reports are strictly delivered to the `User.Email` claim of the Entra ID user who created the schedule.
- **Employee Offboarding:** `ScheduledReportJob` verifies the user's Entra ID status before execution. If the user is disabled/terminated, the schedule is automatically deactivated.
- **Runtime Redaction for Shared Reports:** When an unauthorized user executes a shared report containing restricted fields (e.g., HMDA data), the `RedactionEnforcer` (within the query pipeline) dynamically swaps the restricted SQL column with a redaction string (e.g., `'*** REDACTED ***'`) during execution. The report does not fail, but NPI is structurally protected at the database projection level.

### Whitelist Query Builder (Anti-SQLi)

1. Client selects field IDs (e.g., `"loan_amount"`) — never table or column names.
2. The `QueryPipeline` validates the ID via `FieldResolver` against `schema_config.json` → resolves to `SourceTable` + `SourceColumn`.
3. Unknown field IDs result in immediate rejection of the request.
4. All filter values use ADO.NET `SqlParameter`. No string interpolation.

### Schema Config Validation (Defense-in-Depth)

Because `schema_config.json` contains raw SQL fragments (`SqlTemplate` in date operators, `Sql` in joins), the application performs startup-time validation via `SchemaConfigValidator` to prevent config-driven SQL injection:

1. All `SqlTemplate` values are parsed against an allowlist of SQL functions: `CAST`, `GETDATE`, `DATEADD`, `DATEDIFF`, `DATEFROMPARTS`, `YEAR`, `MONTH`, `DAY`.
2. All `Joins[].Sql` values are validated to match the pattern `(INNER|LEFT) JOIN <TABLE> ON <TABLE>.<COL> = <TABLE>.<COL>`.
3. All `SourceTable` and `SourceColumn` values are validated as simple identifiers (alphanumeric + underscore only).
4. The validator rejects any fragment containing: `DROP`, `INSERT`, `UPDATE`, `DELETE`, `EXEC`, `xp_`, semicolons (`;`), or comment markers (`--`, `/*`).
5. **If validation fails, the application refuses to start** and logs a `Critical`-level event. This prevents a misconfigured or malicious PR from reaching production even if code review is bypassed.

### Query Guardrails (Data Volume & Resource Protection)

To prevent runaway queries from degrading the Empower replica or exhausting Blazor Server memory:

1. **Max Row Limit:** `QueryGuardrails` enforces a configurable maximum row count (default: `50,000`). All generated SQL includes a `TOP(@MaxRows)` clause. The UI displays a warning banner when results are truncated.
2. **Command Timeout:** All ADO.NET `SqlCommand` objects are created with a configurable `CommandTimeout` (default: `30` seconds). Queries exceeding this threshold are terminated at the database level.
3. **Export Streaming:** All exports (Excel, CSV) use `IAsyncEnumerable` to stream rows, avoiding full result-set buffering in server memory. This is critical for Blazor Server where memory is per-circuit.
4. **UI Pagination:** The MudBlazor `DataGrid` enforces server-side pagination (default page size: `100`). The full result set is never loaded into the component tree.

### Development Mode Hardening

The stub authentication handler (`StubAuthenticationHandler`) is a convenience for local development. To prevent accidental deployment to a production environment:

1. The stub handler is only registered when `IHostEnvironment.IsDevelopment()` returns `true`.
2. `Program.cs` includes a startup guard: if `ASPNETCORE_ENVIRONMENT` is `Production` or `Staging` **and** no Entra ID configuration is present, the application **throws on startup** with a descriptive error and logs a `Critical`-level event.
3. When the stub handler is active, the UI renders a persistent `⚠ DEV MODE — Authentication Stubbed` banner that cannot be dismissed.

---

## Configuration as Code: `schema_config.json`

New field requests submitted via Azure DevOps will be implemented by modifying this file. The schema defines `FieldType` (Dimension vs. Measure) to drive aggregations, `Description` to provide user-facing help text in the field picker, and `DefaultRedactionValue` to handle unauthorized access gracefully. Relative date options include both presets and a custom range mode, and are explicitly defined so the UI can dynamically populate filter dropdowns without hardcoding logic.

JSON

```
{
  "Settings": {
    "RelativeDateOperators": [
      { "Id": "today", "Label": "Today", "SqlTemplate": "CAST(GETDATE() AS DATE)" },
      { "Id": "last_7_days", "Label": "Last 7 Days", "SqlTemplate": "DATEADD(day, -7, CAST(GETDATE() AS DATE))" },
      { "Id": "current_month", "Label": "Current Month", "SqlTemplate": "DATEADD(month, DATEDIFF(month, 0, GETDATE()), 0)" },
      { "Id": "ytd", "Label": "Year to Date", "SqlTemplate": "DATEFROMPARTS(YEAR(GETDATE()), 1, 1)" },
      { "Id": "custom_range", "Label": "Custom Range", "SqlTemplate": null }
    ]
  },
  "Joins": [
    {
      "Id": "borrower_join",
      "Sql": "INNER JOIN BORROWER ON LOAN.LOANID = BORROWER.LOANID"
    }
  ],
  "Fields": [
    {
      "Id": "loan_amount",
      "Label": "Loan Amount",
      "Description": "Total original loan amount at origination.",
      "Domain": "Loan",
      "DataType": "currency",
      "FieldType": "Measure",
      "AllowedAggregations": ["SUM", "AVG", "MAX", "MIN"],
      "SourceTable": "LOAN",
      "SourceColumn": "LOAN_AMT"
    },
    {
      "Id": "branch_name",
      "Label": "Branch Name",
      "Description": "Originating branch office name.",
      "Domain": "Organization",
      "DataType": "text",
      "FieldType": "Dimension",
      "SourceTable": "LOAN",
      "SourceColumn": "BRANCH_NAME"
    },
    {
      "Id": "hmda_action",
      "Label": "HMDA Action",
      "Description": "HMDA action taken code (e.g., Originated, Denied, Withdrawn). Restricted to Compliance role.",
      "Domain": "Compliance",
      "DataType": "text",
      "FieldType": "Dimension",
      "SourceTable": "LOAN",
      "SourceColumn": "HMDA_ACT",
      "RolesRequired": "Dashboard.Compliance",
      "DefaultRedactionValue": "'*** REDACTED ***'"
    },
    {
      "Id": "borrower_ssn",
      "Label": "Borrower SSN",
      "Description": "Primary borrower Social Security Number. Restricted to Compliance role.",
      "Domain": "Compliance",
      "DataType": "text",
      "FieldType": "Dimension",
      "SourceTable": "BORROWER",
      "SourceColumn": "SSN",
      "RolesRequired": "Dashboard.Compliance",
      "DefaultRedactionValue": "'*** REDACTED ***'"
    }
  ]
}
```

---

## Scheduled Report Delivery

Users can attach recurring schedules to any saved report. At the scheduled time, Hangfire executes the query and emails the results securely to the user.

### User Interface (`ScheduleDialog.razor`)

- **Frequency:** Daily / Weekly / Monthly
- **Time:** Local time (stored as UTC)
- **Delivery Destination (Read-Only):** _"This report will be emailed to: jdoe@tle.com"_
- **Attachment Format:** Dropdown — Excel (.xlsx) / CSV (default: Excel)
- **Include Inline Preview:** Toggle to include an HTML table preview in the email body (default: on).

### Failure Notifications

When a scheduled report fails to execute, the system notifies the owning user so they are not silently left without their expected data:

- **On First Failure:** The user receives an email from the service mailbox with subject `[TLE Automated Report] ⚠ Delivery Failed: {Report Subject}` explaining the failure and that the system will retry automatically.
- **On Schedule Deactivation (3+ consecutive failures):** The user receives a final email stating the schedule has been paused and must be manually re-enabled from the Schedule Manager.
- **In-App Indicator:** The Schedule Manager displays a red status badge next to any schedule with `last_run_status = 'Failed'`, with the failure reason visible on hover.

### Anti-Phishing Email Templating (`EmailService.cs`)

To prevent the service mailbox (`reports@tle.com`) from being used for internal phishing, the system forces standard templates:

- **Mandatory Subject Prefix:** `[TLE Automated Report] {User Defined Subject}`
- **Mandatory Footer:** _"This is an automated report generated by the TLE Reporting Dashboard. It was scheduled by {User.Name} ({User.Email}). Do not forward this data to unauthorized external parties."_

### Database Schema (`saved_reports`)

SQL

```
CREATE TABLE saved_reports (
    id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    name            NVARCHAR(250)    NOT NULL,
    owner_id        NVARCHAR(128)    NOT NULL,   -- Entra object ID (or 'SYSTEM' for templates)
    owner_email     NVARCHAR(256)    NOT NULL,
    field_ids       NVARCHAR(MAX)    NOT NULL,   -- JSON array of selected field IDs
    filters         NVARCHAR(MAX)    NULL,        -- JSON array of applied filters
    aggregations    NVARCHAR(MAX)    NULL,        -- JSON array of aggregation config
    column_state    NVARCHAR(MAX)    NULL,        -- JSON: column order, widths, visibility
    is_template     BIT              NOT NULL DEFAULT 0,  -- System-provided starter reports
    last_run_at     DATETIME2        NULL,
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### Database Schema (`user_preferences`)

SQL

```
CREATE TABLE user_preferences (
    user_id              NVARCHAR(128) PRIMARY KEY,  -- Entra object ID
    onboarding_completed BIT           NOT NULL DEFAULT 0,
    default_page_size    INT           NOT NULL DEFAULT 100,
    created_at           DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### Database Schema (`report_schedules`)

SQL

```
CREATE TABLE report_schedules (
    id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    report_id         UNIQUEIDENTIFIER NOT NULL REFERENCES saved_reports(id) ON DELETE CASCADE,
    owner_id          NVARCHAR(128)    NOT NULL,   -- Entra object ID 
    owner_email       NVARCHAR(256)    NOT NULL,   -- Locked delivery destination
    cron_expression   NVARCHAR(100)    NOT NULL,   
    subject           NVARCHAR(250)    NOT NULL,
    attachment_format NVARCHAR(10)     NOT NULL DEFAULT 'xlsx',  -- xlsx | csv
    include_preview   BIT              NOT NULL DEFAULT 1,       -- Inline HTML table in email body
    is_active         BIT              NOT NULL DEFAULT 1,
    last_run_at       DATETIME2        NULL,
    last_run_status   NVARCHAR(50)     NULL,       
    consecutive_failures INT           NOT NULL DEFAULT 0,       -- Tracks failures for auto-deactivation
    hangfire_job_id   NVARCHAR(200)    NULL,       
    created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

---

## User Experience

### Visual Design

**Clean Light Theme** — designed to feel like a standard business tool.

|**Element**|**Value**|
|---|---|
|Background|`#FFFFFF` (white)|
|Surface|`#F8F9FA` (light gray)|
|Primary Accent|`#1A73E8` (business blue)|
|Text|`#202124` (near-black)|
|Borders|`1px solid #DADCE0`|

### Landing Page: Report Library (`ReportLibrary.razor`)

The Report Library is the first screen users see after login. It replaces the blank-canvas anti-pattern with an organized home base for all reporting activity.

**Tabs:**

- **My Reports** — saved reports owned by the current user, sorted by last-run date descending. Displays report name, field count, last run timestamp, and schedule status (if any).
- **Shared With Me** — reports shared to the current user by others, with the owner's name displayed. Redaction rules apply at runtime as normal.
- **Templates** — system-provided starter reports (read-only) that users can clone into their own workspace and customize. Pre-built templates are seeded at deployment (see Starter Templates below).
- **Recent** — the last 10 reports the user has run, regardless of ownership, for quick re-access.

**Search & Sort:** A search bar at the top filters across report names. Column headers (Name, Owner, Last Run, Created) are sortable.

**Actions per report:** Run, Edit, Clone, Share, Schedule, Delete (with soft-delete confirmation).

### Starter Templates

To avoid a cold-start problem on day one, the system ships with pre-built report templates that mirror the most common SSRS and Power BI reports being replaced. Templates are defined as seed data in the `saved_reports` table with `is_template = true` and `owner_id = 'SYSTEM'`.

**Initial template set (to be confirmed with Paul Yap during field mapping):**

- Monthly Pipeline by Branch
- Loan Volume YTD
- Funding Summary by Loan Officer
- Compliance: HMDA Action Summary (requires `Dashboard.Compliance`)

Users clone a template to create an editable personal copy. Templates themselves are read-only and managed via source control alongside `schema_config.json`.

### Report Builder (`ReportBuilder.razor`)

The Report Builder is the main workspace for constructing and running reports. It replaces the former single `Dashboard.razor` page with a clearer layout.

**Layout (left-to-right):**

1. **Field Picker panel (left sidebar)** — see Field Discovery below.
2. **Filter bar (top)** — active filters displayed as removable chips. Date filter includes both preset dropdowns and a custom date range picker.
3. **Results area (center)** — `ReportGrid` component with chart toggle above.
4. **Action bar (top-right)** — Run, Save, Export dropdown, Schedule, Share.

### Field Discovery (`FieldPicker.razor`)

With potentially hundreds of Empower fields in production, discoverability is critical. The Field Picker provides:

- **Search:** Real-time text filter across `Label` and `Description` as the user types. Matching text is highlighted.
- **Domain grouping:** Fields are organized under collapsible domain headers (Loan, Organization, Compliance, Borrower, Property, etc.) matching the `Domain` property in `schema_config.json`.
- **Descriptions:** Each field displays its `Description` property (from `schema_config.json`) as a subtitle or tooltip. This is the user's primary way to understand what a field means without calling IT.
- **Role indicators:** Fields requiring elevated roles (e.g., `Dashboard.Compliance`) display a lock icon. If the user lacks the required role, the field is visible but dimmed with a tooltip: _"Requires Compliance access — data will be redacted."_
- **Field type badges:** Dimension fields show a `Dim` badge, Measure fields show a `Σ` badge, so users understand which fields can be aggregated.

### Date Range Filtering (`DateRangeFilter.razor`)

The date filter supports both relative presets and custom ranges:

- **Presets:** Dropdown populated from `Settings.RelativeDateOperators` in `schema_config.json` (Today, Last 7 Days, Current Month, YTD).
- **Custom Range:** When the user selects "Custom Range," two `MudDatePicker` inputs appear for **From** and **To** dates. The `DateFilterTranslator` generates parameterized SQL (`@DateFrom`, `@DateTo`) for custom ranges — no `SqlTemplate` is used; values are passed as `SqlParameter` only.
- **Validation:** The To date must be ≥ the From date. Both dates must be ≤ today. Invalid ranges are rejected client-side with an inline error.

### Report Sharing (`ShareDialog.razor`)

Report sharing enables collaboration while preserving RBAC redaction boundaries.

**Sharing model:**

- **Granularity:** Reports are shared with individual Entra ID users (by name/email lookup) or with Entra ID security groups.
- **Permission level:** Viewer (run only) or Editor (run + modify fields/filters). Only the report owner can delete or transfer ownership.
- **Shared With Me tab:** Shared reports appear in the recipient's Report Library under the "Shared With Me" tab, showing the owner's name.
- **Revocation:** The owner can remove a share at any time from the Share Dialog. Revoked users lose access immediately.
- **Redaction at runtime:** Sharing does not grant the recipient the owner's roles. If a shared report includes compliance fields and the recipient lacks `Dashboard.Compliance`, those columns render as `*** REDACTED ***` per the existing `RedactionEnforcer` logic. No additional logic is required — the pipeline handles this transparently.

**Database schema (`report_shares`):**

SQL

```
CREATE TABLE report_shares (
    id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    report_id       UNIQUEIDENTIFIER NOT NULL REFERENCES saved_reports(id) ON DELETE CASCADE,
    shared_with_id  NVARCHAR(128)    NOT NULL,   -- Entra object ID of user or group
    shared_with_type NVARCHAR(10)    NOT NULL,   -- 'user' or 'group'
    permission      NVARCHAR(10)     NOT NULL DEFAULT 'viewer',  -- viewer | editor
    shared_by_id    NVARCHAR(128)    NOT NULL,   -- Entra object ID of the sharer
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### Grid Interaction (`ReportGrid.razor`)

The results grid wraps MudBlazor's `MudDataGrid` with the following interactive features enabled:

- **Column sorting:** Click any column header to sort ascending/descending. Sort state is preserved when paginating.
- **Column reordering:** Drag column headers to rearrange the display order.
- **Column resizing:** Drag column borders to adjust width.
- **Column visibility:** A column-chooser button (☰) opens a checklist allowing the user to hide/show columns after running a report — useful when a report has many fields but the user wants to focus on a subset.
- **Server-side pagination:** Default page size of 100, with page-size selector (25 / 50 / 100 / 250).
- **Row count display:** Footer shows _"Showing 1–100 of 12,345 rows"_ (or _"Showing 1–100 of 50,000 rows (results truncated)"_ when guardrails apply).

### Export (`ExportMenu.razor`)

The Export dropdown offers two formats:

- **Excel (.xlsx):** Generated via ClosedXML. Includes formatted column headers (bold, auto-width), the report name as the sheet tab name, currency formatting on `currency` fields, and a metadata row with the report name, run date, and user. This is the default and expected primary format for business users.
- **CSV:** Raw comma-separated values with a UTF-8 BOM for Excel compatibility. Intended for data integration or users who need to feed data into other tools.

All exports stream via `IAsyncEnumerable` to avoid buffering the full result set in server memory (see Query Guardrails). Exports respect the current column visibility and sort order from the grid. Redacted values are exported as `*** REDACTED ***` — there is no way to bypass redaction via export.

### Print Support

A dedicated print stylesheet (`wwwroot/css/print.css`) ensures that `Ctrl+P` or a "Print" button produces a clean, usable printout:

- The sidebar, filter bar, and action buttons are hidden via `@media print`.
- The grid renders without pagination — all rows up to the max row limit are included.
- The report name, run date, and user are printed as a header.
- Page breaks are inserted between the chart and the grid.
- Column widths are optimized for landscape orientation.

### Onboarding & Help

**First-run walkthrough:** On the user's first login (tracked via a `user_preferences` flag), a lightweight step-by-step overlay highlights the key areas: Report Library tabs, the "New Report" button, the Field Picker search/descriptions, the filter bar, and the Export menu. The walkthrough can be dismissed and replayed from a "Help" menu item.

**Field-level help:** Every field in the Field Picker displays its `Description` from `schema_config.json`. For fields with `RolesRequired`, the tooltip additionally explains: _"This field requires {RoleName} access. If you do not have this role, values will appear as redacted."_

**Empty-state guidance:** When a user has no saved reports (My Reports tab is empty), the page displays a friendly prompt: _"You haven't created any reports yet. Get started by cloning a Template, or click New Report to build one from scratch."_

### Schedule Failure Awareness

Users should never be silently left without an expected report. The system provides three levels of failure visibility:

1. **In-App:** The Schedule Manager shows a red badge on any schedule with `last_run_status = 'Failed'`. Hovering displays the failure reason and timestamp.
2. **Email on first failure:** An automated email from the service mailbox informs the user that their scheduled report failed and will be retried (see Failure Notifications under Scheduled Report Delivery).
3. **Email on deactivation:** If the schedule is auto-deactivated after 3 consecutive failures, a final email tells the user the schedule is paused and must be re-enabled manually.

---

## Capacity & Resilience

### Blazor Server Circuit Management

Blazor Server holds a persistent SignalR circuit and server-side component state for every open browser tab. For a reporting dashboard where users leave tabs open throughout the workday, circuit management is critical.

- **Circuit Idle Timeout:** Configured via `CircuitOptions.DisconnectedCircuitRetentionPeriod` — default `3 minutes`. Idle circuits are evicted to reclaim server memory.
- **Max Retained Circuits:** Configured via `CircuitOptions.DisconnectedCircuitMaxRetained` — set to a value aligned with expected concurrent users (initial target: `200`).
- **Graceful Reconnection UX:** The Blazor reconnection UI is customized to display _"Reconnecting to server…"_ with a retry countdown. After 3 failed retries, the user is prompted to reload.
- **App Service Scaling:** ARR affinity (sticky sessions) is enabled to pin circuits to their originating instance. If concurrent user count exceeds a single instance ceiling, horizontal scale-out is configured via Azure App Service auto-scale rules based on memory and CPU thresholds.

### Resilience Policies (External Dependencies)

Each external dependency has an explicit failure strategy using `Microsoft.Extensions.Http.Resilience` (Polly v8 integration):

| Dependency | Retry Policy | Circuit Breaker | Timeout | Fallback |
|---|---|---|---|---|
| Empower Replica (ADO.NET) | 2 retries, exponential backoff | Open after 5 failures in 60s | `CommandTimeout = 30s` | UI shows _"Data source temporarily unavailable"_ banner |
| Microsoft Graph API (Email) | 3 retries, exponential backoff + jitter | Open after 5 failures in 120s | 15s per request | Job marked `Failed_Graph`, alert fires, auto-retry next cycle |
| Entra ID (User validation) | 2 retries | Open after 3 failures in 60s | 10s per request | Job deferred, not deactivated (transient Graph outage ≠ user disabled) |

### Hangfire Job Failure Handling

- **Max Retries:** `ScheduledReportJob` is decorated with `[AutomaticRetry(Attempts = 3)]`.
- **Dead Letter Alerting:** After 3 failed attempts, the job moves to the Hangfire `Failed` state. A background monitor checks for failed jobs every 15 minutes and logs a `Warning`-level structured event (`ScheduledJobExhaustedRetries`) including `ReportId`, `OwnerEmail`, and `LastError`.
- **Stale Schedule Detection:** A daily maintenance job scans `report_schedules` for entries where `last_run_status = 'Failed'` for 3+ consecutive runs and sets `is_active = false`, logging a `Warning`-level event.

---

## Observability & SLOs

### Service Level Objectives

| Metric | Target | Measurement |
|---|---|---|
| Dashboard query P95 latency | < 3 seconds | OpenTelemetry histogram on `SqlEmitter.ExecuteAsync` |
| Dashboard query P99 latency | < 8 seconds | OpenTelemetry histogram on `SqlEmitter.ExecuteAsync` |
| Scheduled report delivery success rate | ≥ 99% (7-day rolling) | Hangfire `Succeeded` / (`Succeeded` + `Failed`) |
| SignalR circuit error rate | < 1% | Azure Monitor `SignalR.ConnectionErrors` counter |
| App availability (HTTP 200 on `/health`) | ≥ 99.5% | Azure App Service health check probe |

### OpenTelemetry Tracing

The full query hot path is instrumented with `ActivitySource` spans so that a single trace captures the end-to-end latency from user click to grid render:

`ReportBuilder.razor (user click)` → `FieldResolver` → `JoinResolver` → `RedactionEnforcer` → `AggregationBuilder` → `DateFilterTranslator` → `QueryGuardrails` → `SqlEmitter (ADO.NET)` → `ReportBuilder.razor (grid bind)`

Each span records field count, join count, redacted field count, row count, and execution time.

### Azure Monitor Alerts

| Alert | Condition | Severity |
|---|---|---|
| Query P95 > 5s (sustained 10 min) | OpenTelemetry metric breach | Sev 2 (Warning) |
| Hangfire queue depth > 50 | Hangfire SQL polling | Sev 2 (Warning) |
| Graph API circuit breaker open | Polly telemetry event | Sev 1 (Error) |
| Stub auth handler activated outside Development | Structured log `StubAuthActivated` | Sev 0 (Critical) |
| Schema config validation failure | Structured log `SchemaValidationFailed` | Sev 0 (Critical) |

---

## Build Phases

| **Phase**              | **Scope**                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | **Parallel** |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ |
| **0: Scaffold**        | Aspire solution + Blazor Web project + Worker project + Core shared library + unit test project + E2E test project + NuGet packages (pinned versions) + Playwright install                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               | Sequential   |
| **1: Data/Services**   | **(TDD Required)** Write failing tests first. Then implement models, database contexts (user state + report_shares), service interfaces, `MockDataService`, and `StubAuthHandler`.<br><br>**Core Engine Requirements (Query Pipeline):**<br><br>• **`SchemaService`**: Parse `schema_config.json`. Expose relative date operators, field definitions with `Description`, and domain groupings.<br><br>• **`SchemaConfigValidator`**: Startup validation of all SQL fragments — allowlisted functions only, reject dangerous keywords. Fail startup on violation.<br><br>• **`FieldResolver`**: Validate field IDs against schema, resolve to `SourceTable` + `SourceColumn`. Reject unknown IDs.<br><br>• **`JoinResolver`**: Auto-resolve necessary `JOIN` paths based on the set of selected fields.<br><br>• **`RedactionEnforcer`**: Execution-time RBAC token validation. Swap restricted `SourceColumn` with `DefaultRedactionValue` in the SQL projection for unauthorized users.<br><br>• **`AggregationBuilder`**: `GROUP BY` generation for "Dimension" fields, aggregate functions (SUM/AVG) for "Measure" fields. `NULL`/`0` coalescing for redacted measures.<br><br>• **`DateFilterTranslator`**: Translate UI relative date tokens (e.g., `last_7_days`) into validated runtime SQL functions. Handle `custom_range` via parameterized `@DateFrom`/`@DateTo`.<br><br>• **`QueryGuardrails`**: Enforce max row limit (`TOP(@MaxRows)`), `CommandTimeout`, and pagination constraints.<br><br>• **`SqlEmitter`**: Final parameterized SQL assembly + ADO.NET execution with streaming support for export.<br><br>• **`ExportService`**: Multi-format export — Excel (.xlsx via ClosedXML) and CSV. Streaming via `IAsyncEnumerable`. Respects column visibility, sort order, and redaction.<br><br>• **`SharingService`**: CRUD for `report_shares`, Entra ID user/group lookup, permission enforcement (viewer/editor), revocation.<br><br>• **`ScheduleService` & `EmailService`**: CRUD for `report_schedules`, Hangfire registration, Microsoft Graph integration with Polly resilience policies, and user-facing failure notification emails.<br><br>• **`ScheduledReportJob`** (in Worker project): Hangfire job to execute the query pipeline, format attachment in requested format (xlsx/csv), fire the email, and send failure notifications on error.<br><br>• **Template seeding**: EF Core migration seeds initial starter templates into `saved_reports` with `is_template = true`. | Agent A      |
| **2: UI Components**   | MudBlazor theme, **ReportLibrary** (tabs, search, sort, actions), **ReportBuilder** (layout, action bar), **FieldPicker** (search, domain grouping, descriptions, role indicators), **DateRangeFilter** (presets + custom from/to), **ReportGrid** (sort, reorder, resize, column visibility, pagination), **ShareDialog** (user/group lookup, permission level, revocation), **ExportMenu** (Excel/CSV dropdown), **ScheduleDialog**, **ScheduleManager** (failure badges). Print stylesheet. Transparency/redaction banners. Query-truncation warning banner. Reconnection UI. First-run onboarding walkthrough. Empty-state guidance. **Write Playwright E2E tests** for all scenarios (library, field picker, query flow, grid interactions, date filters, redaction, sharing, schedule, export, pagination, guardrails, print, onboarding, resilience, accessibility).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | Agent B      |
| **3: Config/Plumbing** | AppHost (Web + Worker resources), ServiceDefaults, Program.cs (startup guards, circuit options, Polly policies), appsettings, Azure Monitor Serilog setup, OpenTelemetry tracing on query pipeline, Azure Monitor alert rules                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 | Agent C      |
| **4: Code Review**     | Sr. .NET Developer reviews all code for security, SOX/GLBA controls, test coverage, pipeline stage isolation, resilience policy correctness                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             | Agent D      |
| **5: Integration**     | Verify end-to-end data flow, verify Hangfire job fires from Worker process and delivers specifically to the authenticated user token, verify startup guard rejects Production without Entra ID, verify schema config validator catches malformed SQL fragments                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               | Sequential   |
| **6: E2E Test Gate**   | Run full Playwright suite against the Aspire-hosted dev instance. All E2E scenarios must pass (query flow, redaction, schedule, export, pagination, guardrails, resilience, accessibility). Failure blocks release. Screenshots, traces, and console logs captured as CI artifacts on failure.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               | Sequential   |

**Methodology: Test-Driven Development (TDD)**

All development in Phases 1, 2, and 3 must strictly follow a TDD workflow. Developers are required to write failing xUnit/bUnit tests based on the acceptance criteria _before_ implementing the application code. Pull requests will be rejected in Phase 4 if code is submitted without accompanying tests that cover both happy paths and edge cases (e.g., unauthorized field access attempts). Playwright E2E tests are authored alongside UI components in Phase 2 and executed as a release gate in Phase 6.

---

## Verification Plan

1. **Automated Test Suite:** Run `dotnet test` from the solution root.
    - All unit and integration tests must pass (100% green).
    - **Test Coverage:** Code coverage must meet a minimum threshold of 85%, with 100% coverage required on critical security paths (all `QueryPipeline/` classes and `SchemaService.cs`).
    - **Core Pipeline Tests:**
        - **Redaction Test:** `RedactionEnforcer_GivenUnauthorizedUser_InjectsRedactedStringIntoSelect`
        - **Aggregation Redaction Test:** `AggregationBuilder_GivenUnauthorizedUser_AttemptingToSumRedactedMeasure_InjectsZero`
        - **Bridge Join Test:** `JoinResolver_GivenFieldsFromLoanAndProperty_AutomaticallyInjectsBorrowerBridgeJoin`
        - **Relative Date Test:** `DateFilterTranslator_GivenCurrentMonthFilter_GeneratesCorrectDateAddSqlParameter`
    - **Guardrail Tests:**
        - **Max Row Limit Test:** `QueryGuardrails_GivenDefaultConfig_InjectsTopClauseIntoSql`
        - **Timeout Test:** `SqlEmitter_GivenCommandTimeout_SetsPropertyOnSqlCommand`
    - **Schema Validation Tests:**
        - **Allowlist Pass:** `SchemaConfigValidator_GivenValidConfig_PassesValidation`
        - **Reject Dangerous SQL:** `SchemaConfigValidator_GivenDropInSqlTemplate_ThrowsOnStartup`
        - **Reject Semicolons:** `SchemaConfigValidator_GivenSemicolonInJoinSql_ThrowsOnStartup`
    - **Startup Guard Tests:**
        - **Dev Mode Safe:** `StubAuth_GivenDevelopmentEnvironment_Registers`
        - **Production Hard-Fail:** `StubAuth_GivenProductionWithNoEntraConfig_ThrowsOnStartup`
    - **Pipeline Isolation Tests:** Each pipeline stage is tested in isolation with mock inputs/outputs to verify that a bug in one stage (e.g., `AggregationBuilder`) cannot bypass another (e.g., `RedactionEnforcer`).
2. **Build:** `dotnet build` from solution root — 0 errors, 0 warnings
3. **Audit Log Verification:** Run a query in the UI. Check Azure Monitor (or local logs in Dev Mode) to ensure `DataQueryExecuted` event is logged with `UserEmail` and `FieldsRequested`.
4. **Security/Role Verification:** - Remove `Dashboard.Compliance` from stub claims.
    - Run a shared report that includes HMDA/SSN fields. Verify the UI grid renders `'*** REDACTED ***'` instead of the data, and displays the UI transparency banner.
5. **Scheduled Delivery Verification:**
    - Open a saved report → click Schedule.
    - Confirm the UI does NOT ask for an email address, but displays the logged-in user's email.
    - Trigger the Hangfire job manually **from the Worker process**.
    - Confirm the output payload (in logs) shows delivery restricted to the user's email, and contains the mandatory `[TLE Automated Report]` subject prefix and security footer.
6. **Resilience Verification:**
    - Simulate Empower replica unavailability. Verify the UI renders the _"Data source temporarily unavailable"_ banner and does not throw an unhandled exception.
    - Simulate Microsoft Graph failure. Verify `ScheduledReportJob` retries and logs structured failure events.
    - Verify Hangfire dead-letter alerting fires after 3 exhausted retries.
7. **Startup Guard Verification:**
    - Set `ASPNETCORE_ENVIRONMENT=Production` with Entra ID config removed. Verify the application **refuses to start** and logs `Critical`-level `StubAuthActivated` event.
    - Set `ASPNETCORE_ENVIRONMENT=Production` with a `schema_config.json` containing `DROP TABLE` in a `SqlTemplate`. Verify the application **refuses to start**.
8. **E2E Test Suite (Playwright):** Run `dotnet test --project TleReportingDashboard.Tests.E2E` against the Aspire-hosted dev instance.
    - All Playwright scenarios must pass (100% green).
    - Verify report library landing, field picker search/descriptions, query flow, grid interactions, date filters (presets + custom range), redaction DOM output, sharing flow, schedule dialog lockdown, Excel/CSV export downloads, pagination, guardrail banners, print layout, onboarding walkthrough, resilience banners, and accessibility (zero critical axe-core violations).
    - On failure: screenshots, trace files, and console logs are captured for review.
9. **User Experience Verification (Manual):** Walk through the application as a non-technical user to verify:
    - Report Library loads as the landing page after login with all four tabs (My Reports, Shared With Me, Templates, Recent).
    - Starter templates are present in the Templates tab and can be cloned.
    - Field Picker search narrows results as the user types. `Description` text renders as subtitle/tooltip on every field.
    - Compliance fields display a lock icon and dimmed state for users without `Dashboard.Compliance`.
    - Custom date range picker appears when "Custom Range" is selected. Invalid range (to < from) shows inline error.
    - Excel export produces a `.xlsx` file with bold headers, auto-width columns, currency formatting, and the report name as the sheet tab.
    - Grid column sort, reorder, resize, and hide/show all function without page reload.
    - Sharing a report causes it to appear in the recipient's "Shared With Me" tab. Revoking removes it.
    - `Ctrl+P` produces a clean printout with sidebar/actions hidden and report header visible.
    - First-run onboarding walkthrough appears on initial login and does not reappear after dismissal.
    - Schedule Manager shows red failure badge with hover detail on a failed schedule.

---

## End-to-End Testing (Playwright)

Unit tests (xUnit) and component tests (bUnit) validate logic and markup in isolation, but neither exercises the full browser stack — SignalR circuit lifecycle, real DOM rendering, file downloads, navigation, or the interaction between multiple components on a live page. Playwright fills this gap with automated browser tests that run against the actual application.

### Test Infrastructure

- **`AppFixture`** starts the Aspire `AppHost` in Development mode (stub auth, in-memory data) as a shared class fixture. All E2E tests run against this live instance — no external dependencies required.
- **Claim toggling:** `StubClaimsOverride` allows individual tests to modify the stub user's roles (e.g., remove `Dashboard.Compliance`) via a test-only API endpoint exposed only when `IsDevelopment()` is true. This enables redaction and RBAC scenarios without restarting the app.
- **Browser matrix:** CI runs tests against Chromium. Local runs can optionally target Firefox and WebKit via `playwright.config.ts`.
- **Parallelism:** Tests are isolated by page state (each test creates a fresh `BrowserContext`), enabling full parallel execution.

### Core E2E Test Scenarios

| Test Class | What It Validates |
|---|---|
| `ReportLibraryTests` | Login → Report Library loads as the landing page → My Reports / Shared With Me / Templates / Recent tabs render → clicking a template opens Clone dialog → search filters report list. Verifies the landing experience end-to-end. |
| `FieldPickerTests` | Open Report Builder → Field Picker renders domain groups → search filters fields in real time → field descriptions/tooltips display → lock icon appears on compliance fields → selecting a field adds it to the report. |
| `QueryFlowTests` | Select 2–3 fields → click Run → grid renders with correct column headers and row data from mock loans. Verifies the full SignalR round-trip from click to rendered grid. |
| `GridInteractionTests` | Run a report → click column header to sort → drag column to reorder → resize column width → open column chooser and hide a column → assert hidden column is no longer in DOM. |
| `DateFilterTests` | Select a relative preset → run → verify filter chip shows preset label. Switch to Custom Range → enter from/to dates → run → verify parameterized filter applies. Attempt invalid range (to < from) → assert inline validation error. |
| `RedactionFlowTests` | Remove `Dashboard.Compliance` via claim toggle → run a shared report containing HMDA/SSN fields → assert grid cells contain `*** REDACTED ***` → assert transparency banner is visible in the DOM. |
| `SharingFlowTests` | Save a report → open Share Dialog → add a user with Viewer permission → switch to the shared user's context → verify report appears in "Shared With Me" tab → verify the owner can revoke the share. |
| `ScheduleFlowTests` | Open a saved report → click Schedule → assert the email field is present, read-only, and displays the stub user's email → assert no free-text recipient input → assert attachment format dropdown offers Excel/CSV → assert failure badge renders on a schedule with `Failed` status. |
| `ExportTests` | Run a report → click Export → select Excel → intercept download → verify `.xlsx` file is non-empty with correct headers. Repeat for CSV (verify headers and row count). Verify redacted columns export as `*** REDACTED ***` in all formats. |
| `PaginationTests` | Run a report returning > 100 rows → assert pagination controls render → navigate to page 2 → assert grid content changes → change page size to 250 → assert page size updates. Confirms server-side pagination is wired end-to-end. |
| `GuardrailBannerTests` | Run a report that exceeds the max row limit → assert the truncation warning banner is visible and displays the configured limit. |
| `PrintTests` | Run a report → trigger print preview (or assert print stylesheet is linked) → verify sidebar and action buttons have `display:none` in print media → verify report name header is visible in print layout. |
| `OnboardingTests` | Login as a new user (no `user_preferences` flag) → assert first-run walkthrough overlay renders → step through all walkthrough steps → dismiss → assert walkthrough does not reappear on next navigation. Assert Help menu "Replay Walkthrough" re-triggers it. |
| `ResilienceTests` | Trigger a query when the mock data source is configured to throw → assert the _"Data source temporarily unavailable"_ banner renders → assert no unhandled exception dialog or broken SignalR circuit. |
| `AccessibilityTests` | Run an axe-core accessibility scan (via `@axe-core/playwright`) on ReportLibrary, ReportBuilder, ShareDialog, and ScheduleDialog. Assert zero critical or serious violations. |

### CI Integration

- E2E tests run in the CI pipeline **after** Phase 5 (Integration) passes, as a dedicated `playwright` stage.
- Tests execute headless in the CI agent. Playwright browsers are installed via `npx playwright install --with-deps chromium` in the pipeline setup step.
- **Failure artifacts:** On test failure, Playwright captures a screenshot, a trace file (viewable in Playwright Trace Viewer), and the browser console log. These are published as pipeline artifacts for debugging.
- **Gate:** The pipeline fails if any E2E test fails. E2E tests are not optional and cannot be skipped without explicit approval from the code review owner (Agent D).

---

## Development Mode (Zero External Dependencies)

When `ASPNETCORE_ENVIRONMENT=Development` and no database connection strings or Entra ID config are present, the app runs fully self-contained. **This mode is guarded by environment checks and cannot activate in Production or Staging** (see Development Mode Hardening above).

### Stubbed Authentication

- Custom `StubAuthenticationHandler` auto-authenticates every request as "Dev User" (dev@tle.com).
- Provides realistic claims: name, email, object ID, and all app roles (`Dashboard.User`, `Dashboard.Compliance`).
- **Guard:** Only registered when `IsDevelopment()` is `true`. If `ASPNETCORE_ENVIRONMENT` is `Production` or `Staging` and Entra ID is unconfigured, the application **refuses to start**.
- **Visual Indicator:** A persistent `⚠ DEV MODE` banner renders at the top of the UI when stub auth is active.

### Stubbed Data & Email

- App reads the local `schema_config.json` and runs queries against in-memory mock loans.
- `StubEmailService` writes the full email payload to the console instead of routing to Microsoft Graph.

Developers can run the full application with `dotnet run` from the AppHost — no SQL Server, no Entra ID app registration required.

---

## Production Blockers

|**Blocker**|**Owner**|**Status**|
|---|---|---|
|Empower replica credentials|DBA / Deepak|Pending|
|Verify initial field mapping for `schema_config.json`|Paul Yap|Pending|
|Confirm starter template report definitions (fields, filters, names)|Paul Yap|Pending|
|Provide field `Description` text for all reportable Empower fields|Paul Yap|Pending|
|Entra ID app registration (user SSO)|IT Ops|Pending|
|Microsoft Graph app registration + `Mail.Send` permission|IT Ops|Pending|
|Provision service mailbox (`reports@tle.com` or equivalent)|IT Ops|Pending|
|Provision Azure Log Analytics Workspace (for Serilog)|IT Ops|Pending|
|Provision Azure App Service for Hangfire Worker (separate from Web)|IT Ops|Pending|
|Configure Azure Monitor alert rules (see Observability & SLOs)|IT Ops|Pending|

---

## Dependencies

All packages are pinned to exact versions for reproducible builds (SOX change-management requirement). Version upgrades are managed via Dependabot PRs with peer review.

|**Package**|**Version**|**Project**|
|---|---|---|
|MudBlazor|9.0.0|Web|
|Microsoft.EntityFrameworkCore.SqlServer|10.0.0|Web, Worker|
|Microsoft.Identity.Web|3.8.0|Web, Worker|
|Microsoft.Graph|5.74.0|Web, Worker|
|Microsoft.Extensions.Http.Resilience|9.4.0|Web, Worker|
|ClosedXML|0.104.1|Web, Worker|
|Hangfire.AspNetCore|1.8.17|Web (client only), Worker (server)|
|Hangfire.SqlServer|1.8.17|Web, Worker|
|Serilog.AspNetCore|9.0.0|Web, Worker|
|Serilog.Sinks.AzureAnalytics|5.0.0|Web, Worker|
|Aspire.Hosting.AppHost|13.2.0|AppHost|
|OpenTelemetry.Extensions.Hosting|1.12.0|ServiceDefaults|
|OpenTelemetry.Instrumentation.AspNetCore|1.12.0|ServiceDefaults|
|OpenTelemetry.Instrumentation.Http|1.12.0|ServiceDefaults|
|OpenTelemetry.Exporter.OpenTelemetryProtocol|1.12.0|ServiceDefaults|
|xunit|2.9.3|Tests|
|bUnit|2.0.0|Tests|
|Moq|4.20.72|Tests|
|Microsoft.Playwright|1.50.0|Tests.E2E|
|@axe-core/playwright|4.10.0|Tests.E2E (npm)|