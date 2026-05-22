---
tags:
  - entity/tle
  - project/active
  - type/documentation
created: 2026-04-06
status: POC Complete
---

# TLE Reporting Dashboard — C# / Blazor Build Guide

Developer handoff document for the TLE self-service reporting dashboard.

## What This Is

A drag-and-drop reporting dashboard that lets TLE business users create reports from Empower LOS data without involving IT. Built with C# and Blazor Server — one language, one project, one deployment.

**Replaces:** Power BI ($20/user/month PPU) and SSRS ticket requests.

## Tech Stack

| Layer | Technology | Why |
|-------|-----------|-----|
| UI Framework | Blazor Web App (.NET 8, Server interactivity) | Real-time via SignalR, no WASM download, direct DB access |
| Charts | Pure SVG rendered in Blazor | Zero JS chart dependencies |
| Data Access | Dapper + Microsoft.Data.SqlClient | Micro-ORM, full SQL control, matches whitelist query pattern |
| Auth | Microsoft.Identity.Web | Entra ID SSO, one NuGet package |
| Caching | IMemoryCache | 5-min TTL for field config |
| Deploy | Single Azure App Service | No separate SPA + API deployment |

## Prerequisites

- .NET 10 SDK (`dotnet --version` → 10.0.x)
- SQL Server (only for production — dev mode uses mock data)
- Git

## How to Run (Dev Mode)

Dev mode uses mock data — no database, no Entra ID, no configuration needed.

The app detects empty connection strings in `appsettings.json` and automatically registers `MockDataService` for all services.

## How to Build

```bash
dotnet build          # Compile
dotnet run            # Run in dev mode
dotnet publish -c Release -o ./publish   # Production build
```

## Project Structure

```
tle-reporting-dashboard-blazor/
│
├── Program.cs                           # DI registration, middleware, service wiring
├── TleReportingDashboard.csproj         # .NET 8, NuGet packages
├── appsettings.json                     # Connection strings + Entra ID config
│
├── Models/                              # Data contracts
│   ├── FieldConfig.cs                   # Field whitelist entry (id, label, domain, source table/column)
│   ├── JoinConfig.cs                    # Pre-defined table JOIN paths
│   ├── SavedReport.cs                   # Persisted report with JSON config
│   ├── ReportConfig.cs                  # Report state: fields, chart type, filters
│   ├── QueryRequest.cs                  # Query input: fields, filters, sort, pagination
│   ├── QueryResponse.cs + ColumnMeta    # Query output: typed columns + row arrays
│   └── DomainGroup.cs + FieldDefinition # Schema grouped by Empower domain
│
├── Data/
│   └── DbConnectionFactory.cs           # Dual SqlConnection factory (Empower + Config DB)
│
├── Services/                            # Business logic
│   ├── ISchemaService.cs                # Interface: field config + domain grouping
│   ├── IQueryService.cs                 # Interface: execute queries
│   ├── IReportService.cs                # Interface: saved report CRUD
│   ├── SchemaService.cs                 # Reads field_config/join_config, caches 5 min, role filtering
│   ├── QueryBuilder.cs                  # ★ SECURITY CORE: whitelist → parameterized SQL via Dapper
│   ├── QueryService.cs                  # Orchestrator: validate → build SQL → execute → format
│   ├── ReportService.cs                 # CRUD for saved_reports table
│   └── MockDataService.cs              # Implements all 3 interfaces with 50 mock loans
│
├── Components/
│   ├── App.razor                        # HTML shell, CSS/JS references
│   ├── Routes.razor                     # Blazor router
│   ├── _Imports.razor                   # Global usings
│   ├── Layout/
│   │   └── MainLayout.razor             # Minimal layout (dashboard is full-page)
│   ├── Pages/
│   │   ├── Dashboard.razor              # Main page — all state management, 3-column grid
│   │   └── Error.razor                  # Error page
│   └── Shared/
│       ├── FieldPicker.razor            # Left sidebar: +/× field selection, search, domain groups
│       ├── HeaderFilters.razor          # Configurable filter bar: date range, multi-select, add/remove
│       ├── KpiBar.razor                 # Configurable KPI cards: 10 metrics, add/remove
│       ├── FieldChipBar.razor           # Active field pills with × remove, Clear All
│       ├── DataTable.razor              # Sortable columns, drag-to-reorder, typed formatting, pagination
│       ├── ChartBuilder.razor           # SVG Bar/Line/Pie charts, X/Y axis selection
│       ├── SavedReports.razor           # Full CRUD: save, load, rename, update, share, delete
│       └── ExportButton.razor           # CSV export via JS interop
│
├── wwwroot/
│   ├── app.css                          # Dark industrial theme (CSS custom properties)
│   └── js/
│       └── export.js                    # CSV download + column drag interop
│
└── sql/                                 # Config DB schema + seed scripts
    ├── 001_field_config.sql
    ├── 002_join_config.sql
    ├── 003_saved_reports.sql
    └── seed_empower_fields.sql
```

## Architecture

```
Browser ←──SignalR──→ Blazor Server (.NET 10)
                         │
                         ├── SchemaService (IMemoryCache, 5-min TTL)
                         │       └── Dapper → TLE_ReportingConfig DB (field_config, join_config)
                         │
                         ├── QueryService
                         │       ├── QueryBuilder (whitelist → parameterized SQL)
                         │       └── Dapper → Empower Read-Only Replica
                         │
                         └── ReportService
                                 └── Dapper → TLE_ReportingConfig DB (saved_reports)
```

No separate API layer — Blazor Server components call services directly via DI. SignalR handles the browser ↔ server communication.

## Features

### Field Management
- **Field Picker** (left sidebar) — Fields grouped by Empower domain (Loan, Borrower, Property, Dates, Team). Click **+** to add, **×** to remove. Search to filter.
- **Field Chip Bar** — Active fields shown as blue pills above the table. Click **×** on any chip or **Clear All** to reset. Clearing all fields also clears the table data.

### Filters
- **Date Range** — Select date field + from/to. Has **×** to remove the entire date filter.
- **Loan Officer, Branch** — Shown by default. Each has **×** to remove.
- **Dynamic Filters** — Click **"+ Add Filter"** to add any text field (Loan Type, Channel, etc.). All filters are removable.
- **Clear Filters** — Resets all filter values.

### KPI Cards
- 4 default KPIs: Total Volume (gold), Avg Credit Score (blue), Funded Count (green), Avg Cycle Time (orange)
- 10 total available: + Avg Loan Amount, Avg LTV, Avg Rate, Avg DTI, Pipeline Count, Avg Monthly Income
- **×** button always visible (circular, turns red on hover). Click **"+ Add KPI"** to add more.

### Data Table
- Click column header to **sort** (asc → desc → none)
- **Drag column headers** to reorder (⠿ handle icon, blue left-border drop indicator)
- Typed formatting: currency ($1,234.00), percent (4.250%), date (MM/DD/YYYY), integer (1,234)
- Pagination with Prev/Next and row count

### Charts
- **Bar, Line, Pie** — pure SVG, no JS library
- Select X-axis and Y-axis fields from dropdowns
- Dark-themed with accent colors

### Saved Reports
All reports (My Reports + Shared Reports) show these actions:
- **Rename** — Click Rename → inline text input with **Save/Cancel** buttons
- **Update** — Green button. Overwrites report config with current fields, filters, chart settings
- **Share/Unshare** — Toggle sharing (blue when shared)
- **Delete** — Red on hover
- Loading a saved report **always defaults to Table view**

### Export
- **CSV** — Client-side via JS interop. Proper escaping, instant download.

## Security Architecture

### Whitelist Query Builder (`Services/QueryBuilder.cs`)

This is the most security-critical file in the project.

**How it works:**
1. Client selects field IDs like `"loan_amount"`, `"credit_score"` — never table or column names
2. QueryBuilder looks up each ID in `field_config` table → gets `source_table` + `source_column`
3. Unknown field IDs → entire request rejected
4. All filter values go through Dapper `DynamicParameters` — zero string interpolation
5. JOINs come from `join_config` table — no ad-hoc JOINs possible
6. Pagination capped at 500 rows, query timeout 30s

**What this prevents:**
- SQL injection (parameterized everything)
- Unauthorized field access (whitelist-only)
- PII exposure (SSN, DOB never in field_config)
- Full table scans (pagination enforced)

### GLBA Compliance
- No PII fields in field_config — if it's not in the whitelist, it can't be queried
- Read-only connection to Empower replica (db_datareader only)
- Role-based field filtering via Entra ID app roles
- Query audit logging (user, timestamp, fields, row count)

## Service Registration (Program.cs)

```csharp
// Auto-detect: mock mode when no DB configured
var empowerConnStr = builder.Configuration.GetConnectionString("EmpowerReplica");
if (string.IsNullOrEmpty(empowerConnStr))
{
    // Dev mode: mock data — no DB needed
    builder.Services.AddSingleton<MockDataService>();
    builder.Services.AddSingleton<ISchemaService>(sp => sp.GetRequiredService<MockDataService>());
    builder.Services.AddSingleton<IQueryService>(sp => sp.GetRequiredService<MockDataService>());
    builder.Services.AddSingleton<IReportService>(sp => sp.GetRequiredService<MockDataService>());
}
else
{
    // Production: real DB connections
    builder.Services.AddScoped<ISchemaService, SchemaService>();
    builder.Services.AddScoped<IQueryService, QueryService>();
    builder.Services.AddScoped<IReportService, ReportService>();
}
```

To switch from mock to real data, add connection strings to `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "EmpowerReplica": "Server=<host>;Database=<db>;User Id=<reader>;Password=<pass>;TrustServerCertificate=true;",
    "ConfigDb": "Server=<host>;Database=TLE_ReportingConfig;User Id=<user>;Password=<pass>;TrustServerCertificate=true;"
  }
}
```

## Mock Data

`MockDataService.cs` provides:
- **50 seeded mock loans** with realistic Empower data (amounts $185K–$825K, scores 620–800, types Conventional/FHA/VA/USDA/Jumbo)
- **34 fields** across 5 domains
- **3 demo reports** (Pipeline Summary, Funded by Officer, Branch Performance)
- Implements `ISchemaService`, `IQueryService`, `IReportService` — full filtering, sorting, pagination works against in-memory data

## Adding a New Reportable Field (Production)

No code deploy needed:
```sql
INSERT INTO field_config (id, label, domain, data_type, source_table, source_column, sort_order)
VALUES ('investor_name', 'Investor', 'Pipeline & Status', 'text', 'LOAN', 'INVESTOR', 50);
```
Field appears in the picker within 5 minutes (cache TTL).

To gate a field behind a role:
```sql
UPDATE field_config SET roles_required = 'Dashboard.Compliance' WHERE id = 'hmda_action';
```

## Deployment (Azure App Service)

```bash
# Build for production
dotnet publish -c Release -o ./publish

# Create App Service
az webapp create --name tle-dashboard --resource-group ralis-apps --runtime "DOTNETCORE:8.0"

# Set connection strings
az webapp config connection-string set --name tle-dashboard --settings \
  EmpowerReplica="Server=...;Database=...;..." \
  ConfigDb="Server=...;Database=TLE_ReportingConfig;..."

# Deploy
az webapp deploy --name tle-dashboard --src-path ./publish
```

## How This Was Built — Claude Code Walkthrough

This dashboard was built using Claude Code as the AI engineering partner. This section is a step-by-step walkthrough so any developer on the team can replicate this process for future projects.

### What Is Claude Code?

Claude Code is a CLI/IDE tool from Anthropic that acts as an AI pair programmer. It can read your codebase, write files, run commands, and manage complex multi-file builds. Key concepts:

- **Plan Mode** (`/plan`) — Forces Claude to research and design before writing any code. No files are created or edited until you approve the plan.
- **Agents** — Specialized sub-processes Claude launches for parallel work. An "Explore agent" searches code. A "Plan agent" designs architecture. A "Build agent" writes files.
- **Interactive QA** — After building, you test in the browser and describe what's wrong. Claude reads the components, finds the bug, fixes it, and the dev server hot-reloads.

### Step 1: Enter Plan Mode

Start by typing `/plan` in Claude Code, then describe what you want:

```
Help me build an internal drag-and-drop reporting dashboard for TLE
that lets business users create reports from Empower LOS data without
involving IT. Target parity with Power BI's self-service UX but leaner,
owned infrastructure, no per-seat licensing.

Stack: C# Blazor Server + Dapper
DB: Read-only SQL replica of Empower LOS (existing)
Auth: Entra ID SSO (existing)
```

**What happens:** Claude enters read-only mode. It cannot edit files — only research and ask questions.

### Step 2: Claude Explores the Codebase

Claude launched 3 **Explore agents** in parallel to understand the existing environment:

| Agent | What It Searched | What It Found |
|-------|-----------------|---------------|
| Agent 1 | Existing TLE code, Empower integrations | Empower diagnostic skill with SQL queries, ESYS_TYPEDEFS field registry |
| Agent 2 | SSRS meeting notes, reporting strategy docs | Power BI migration plan, security concerns, compliance gaps |
| Agent 3 | Project patterns, deployment configs | CSBK Portal Connect (same Azure deployment pattern), TKG SQL migrations |

This took ~30 seconds. Claude now knows your codebase, your constraints, and your existing patterns.

### Step 3: Claude Asks Clarifying Questions

Before writing a single line of code, Claude asked:

1. **"Where is the frontend?"** → Answer: Scaffold fresh
2. **"Which API runtime?"** → Answer: .NET 8 / Blazor Server
3. **"Config DB location?"** → Answer: SQL Server on-prem
4. **"Which ORM?"** → Answer: Dapper

These answers locked the tech decisions. Claude won't guess — it asks.

### Step 4: Claude Writes the Plan

A **Plan agent** designed the full implementation:
- Project structure (every file path)
- Database schema (field_config, join_config, saved_reports)
- Service interfaces (ISchemaService, IQueryService, IReportService)
- Security architecture (whitelist query builder, parameterized SQL, role gating)
- Mock data strategy (works without DB)

The plan was written to a file for review. You read it, push back on anything wrong, then approve.

### Step 5: Parallel Build

Once approved, Claude exited plan mode and launched **3 build agents simultaneously**:

| Agent | What It Built | Files | Time |
|-------|--------------|-------|------|
| Data/Services Agent | Models, DbConnectionFactory, QueryBuilder, SchemaService, QueryService, ReportService, MockDataService | 16 files | ~8 min |
| UI Components Agent | All .razor components, app.css dark theme, export.js | 17 files | ~19 min |
| Config Agent | Program.cs, appsettings, interfaces, _Imports.razor, App.razor | 10 files | ~5 min |

All three agents worked on different files — no merge conflicts. Total wall clock: ~19 minutes (not 32 minutes sequential).

### Step 6: Build Verification

```bash
dotnet build
# Build succeeded. 0 Warning(s) 0 Error(s)

dotnet run
# → http://localhost:5200 — full dashboard with mock data
```

### Step 7: Interactive QA

Opened the browser, tested each feature, and described issues directly to Claude:

| What I Said | What Claude Did |
|-------------|----------------|
| "The select fields doesn't work" | Read FieldPicker.razor, found click handler conflict, fixed pointer event tracking |
| "Clear filter button does not function" | Traced the event chain, found disabled styling issue, made always clickable |
| "Need ability to move columns around" | Added HTML5 drag-and-drop to DataTable headers with visual drop indicator |
| "Add delete for filters and KPI" | Made × buttons always visible with circular styling, red hover |
| "Enable ability to edit reports" | Added Rename (inline Save/Cancel), Update (green), Share/Delete on all reports |
| "Load report shouldn't switch to chart" | One-line fix: `viewTab = "table"` in HandleLoadReport |

Each fix: Claude reads the file → identifies root cause → edits → verifies build passes. Blazor Server hot-reloads automatically.

### Step 8: Document

Claude generated this build guide and the Obsidian project documentation.

### Reproducing This Workflow

For your next project:

```
1. Open Claude Code in your project directory
2. Type /plan
3. Describe what you want — include constraints, stack, existing systems
4. Let Claude explore and ask questions (don't skip this)
5. Review the plan file — push back on anything wrong
6. Approve → Claude exits plan mode and builds
7. Run the app → test in browser → describe issues
8. Iterate until done
9. Ask Claude to document it
```

**Key principles:**
- **Plan before code** — `/plan` prevents wasted work. Every file has a purpose before it's created.
- **Let Claude ask questions** — Don't pre-answer everything. Claude's questions often surface decisions you haven't made yet.
- **Parallel agents** — Claude builds multiple parts simultaneously. A 30-minute sequential build becomes a 10-minute parallel build.
- **Test in the browser, not in your head** — Mock data means the app works from minute one. Find real bugs, not theoretical ones.
- **Iterate fast** — Describe the problem, not the solution. "The × button is invisible" is better than "change the CSS opacity to 1".

## Blockers for Production

| Blocker | Owner | Status |
|---------|-------|--------|
| Empower replica credentials | DBA / Deepak | Pending |
| Verify field mapping from `ESYS_TYPEDEFS` | Paul Yap | Pending |
| Entra ID app registration | IT Ops | Pending |
| Create `TLE_ReportingConfig` database | DBA | Pending |
| Network: Azure App Service → on-prem SQL | IT Ops | Pending |

## Config DB Setup

Run these SQL scripts in order against `TLE_ReportingConfig`:
```
1. sql/002_join_config.sql    ← must run first (FK dependency)
2. sql/001_field_config.sql
3. sql/003_saved_reports.sql
4. sql/seed_empower_fields.sql ← verify column names vs ESYS_TYPEDEFS first
```

Paul Yap should verify actual Empower column names:
```sql
SELECT VARNAME, DESCRIP, TABLENAME FROM ESYS_TYPEDEFS
WHERE TABLENAME IN ('LOAN','BORROWER','PROPERTY','LOANMILESTONES')
ORDER BY TABLENAME, VARNAME
```

## Related

- [[2026-04-04-SSRS-Meeting-Analysis]] — Meeting that kicked this off
- [[Empower-API-Integration-Plan]] — Empower API strategy
