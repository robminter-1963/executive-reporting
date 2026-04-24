-- ============================================================================
-- Phase 3: Dashboards become shared-per-company (admin-defined)
--
-- Drops the user_id scope from RPT_master_dashboard_tabs and
-- RPT_master_dashboard_tiles. Every user with access to a company now sees
-- the same layout; only admins can edit it.
--
-- Seeding rule (per user's Phase 3 direction): "pick the existing config
-- for the seeding." For each company, we pick the user_id whose earliest
-- tab (lowest id) is the oldest in that company, and delete every other
-- user's tabs and tiles. Tiles are pruned first because the rollback path
-- needs the row counts to line up with tabs.
--
-- IMPORTANT: Non-reversible for the discarded rows — the rollback script
-- only restores the user_id column shape, not the deleted data.
--
-- Idempotent: safe to re-run. The seed + delete passes are gated on the
-- user_id column still existing; the ALTER DROP is gated on the column
-- still existing (second run becomes a no-op).
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. Seed + prune: keep only one user's rows per company ─────────────────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs')
             AND name = 'user_id')
BEGIN
    ;WITH TabFirstPerOwner AS (
        SELECT company_id, user_id, MIN(id) AS first_tab_id
          FROM EMPOWER.RPT_master_dashboard_tabs
         GROUP BY company_id, user_id
    ),
    Ranked AS (
        SELECT company_id, user_id,
               ROW_NUMBER() OVER (PARTITION BY company_id ORDER BY first_tab_id) AS rn
          FROM TabFirstPerOwner
    ),
    KeepUsers AS (
        SELECT company_id, user_id FROM Ranked WHERE rn = 1
    )
    DELETE tl
      FROM EMPOWER.RPT_master_dashboard_tiles tl
      LEFT JOIN KeepUsers k
        ON k.company_id = tl.company_id AND k.user_id = tl.user_id
     WHERE k.user_id IS NULL;

    ;WITH TabFirstPerOwner AS (
        SELECT company_id, user_id, MIN(id) AS first_tab_id
          FROM EMPOWER.RPT_master_dashboard_tabs
         GROUP BY company_id, user_id
    ),
    Ranked AS (
        SELECT company_id, user_id,
               ROW_NUMBER() OVER (PARTITION BY company_id ORDER BY first_tab_id) AS rn
          FROM TabFirstPerOwner
    ),
    KeepUsers AS (
        SELECT company_id, user_id FROM Ranked WHERE rn = 1
    )
    DELETE tb
      FROM EMPOWER.RPT_master_dashboard_tabs tb
      LEFT JOIN KeepUsers k
        ON k.company_id = tb.company_id AND k.user_id = tb.user_id
     WHERE k.user_id IS NULL;
END
GO

-- ── 2. Drop any indexes that reference user_id ─────────────────────────────
-- Covers the covering index added in multi_company_phase1 plus whatever the
-- original table definition shipped with. Discovered at runtime so name
-- drift doesn't trip us up.
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + N'DROP INDEX ' + QUOTENAME(i.name) + N' ON EMPOWER.' + QUOTENAME(t.name) + N';' + CHAR(10)
  FROM sys.indexes i
  JOIN sys.tables  t ON t.object_id = i.object_id
  JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
 WHERE t.name IN ('RPT_master_dashboard_tabs', 'RPT_master_dashboard_tiles')
   AND c.name = 'user_id'
   AND i.is_primary_key = 0;
IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO

-- ── 3. Drop the user_id columns ────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs')
             AND name = 'user_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs DROP COLUMN user_id;
END
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
             AND name = 'user_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles DROP COLUMN user_id;
END
GO

-- ── 4. Covering indexes keyed only on company_id ───────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tabs_company'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs'))
BEGIN
    CREATE INDEX IX_master_dashboard_tabs_company
        ON EMPOWER.RPT_master_dashboard_tabs (company_id, sort_order);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tiles_company_tab_shared'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
BEGIN
    CREATE INDEX IX_master_dashboard_tiles_company_tab_shared
        ON EMPOWER.RPT_master_dashboard_tiles (company_id, tab_id, sort_order);
END
GO
