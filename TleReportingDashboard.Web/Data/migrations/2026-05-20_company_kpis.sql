-- ─────────────────────────────────────────────────────────────────────────────
-- Company KPIs — per-company KPI cards that render in a horizontal band
-- above the Master Dashboard's tab strip. Scope is the company, not the tab,
-- so each company has one shared list of KPIs visible across all its tabs.
--
-- Two changes:
--   1. RPT_company_kpis — one row per KPI card, with the connection + primary
--      table + field + aggregation + optional period filter that drives the
--      single-row aggregation query at render time.
--   2. RPT_companies.show_kpi_band — per-company on/off toggle. When 0, the
--      band is hidden regardless of how many KPIs are defined. Default 1 so
--      newly-defined KPIs are immediately visible.
--
-- Render-format isn't stored — auto-derived from the field's DataType at
-- render time. Matches the existing pattern where field formatting lives
-- on FieldConfig (currency, percent, integer, etc.).
--
-- Idempotent: re-runs cleanly on databases that already have the table /
-- column. Mirrors the library_sections migration's style.
-- ─────────────────────────────────────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER'
                 AND TABLE_NAME = 'RPT_company_kpis')
BEGIN
    CREATE TABLE EMPOWER.RPT_company_kpis (
        id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RPT_company_kpis PRIMARY KEY,
        company_id        UNIQUEIDENTIFIER NOT NULL,
        connection_id     UNIQUEIDENTIFIER NOT NULL,
        primary_table     NVARCHAR(200)    NOT NULL,
        label             NVARCHAR(120)    NULL,
        field_id          NVARCHAR(120)    NOT NULL,
        aggregation       NVARCHAR(10)     NOT NULL CONSTRAINT DF_RPT_company_kpis_agg DEFAULT 'sum',
        date_field_id     NVARCHAR(120)    NULL,
        period            NVARCHAR(20)     NULL,
        compare_previous  BIT              NOT NULL CONSTRAINT DF_RPT_company_kpis_compare DEFAULT 0,
        col_span          INT              NOT NULL CONSTRAINT DF_RPT_company_kpis_colspan DEFAULT 3,
        sort_order        INT              NOT NULL CONSTRAINT DF_RPT_company_kpis_sort DEFAULT 0,
        created_at        DATETIME         NOT NULL CONSTRAINT DF_RPT_company_kpis_created DEFAULT GETDATE(),
        created_by_email  NVARCHAR(256)    NULL,
        CONSTRAINT FK_RPT_company_kpis_company
            FOREIGN KEY (company_id)
            REFERENCES EMPOWER.RPT_companies(id) ON DELETE CASCADE,
        -- NO ACTION (not CASCADE) on the connection FK because RPT_company_connections
        -- itself cascades from RPT_companies, and SQL Server refuses two cascade paths
        -- into the same row. If a connection is deleted, KPIs that reference it are
        -- left in place but their aggregation query fails — the card renders an em-dash.
        -- The company-level cascade above still cleans up KPIs when an entire company
        -- is removed.
        CONSTRAINT FK_RPT_company_kpis_connection
            FOREIGN KEY (connection_id)
            REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE NO ACTION,
        CONSTRAINT CK_RPT_company_kpis_agg
            CHECK (aggregation IN ('sum','count','avg','min','max')),
        CONSTRAINT CK_RPT_company_kpis_period
            CHECK (period IS NULL OR period IN ('mtd','qtd','ytd','last_30d','last_90d'))
    );
END
GO

-- Listing index for the typical "render this company's KPI band in order" query.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_company_kpis_company_sort'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_company_kpis'))
BEGIN
    CREATE INDEX IX_company_kpis_company_sort
        ON EMPOWER.RPT_company_kpis(company_id, sort_order);
END
GO

-- Per-company on/off toggle. Default ON so admins who set up KPIs see them
-- immediately; flip OFF in the Manage KPIs dialog to temporarily hide the
-- band without losing the definitions.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_companies')
                 AND name = 'show_kpi_band')
BEGIN
    ALTER TABLE EMPOWER.RPT_companies
        ADD show_kpi_band BIT NOT NULL CONSTRAINT DF_RPT_companies_show_kpi_band DEFAULT 1;
END
GO
