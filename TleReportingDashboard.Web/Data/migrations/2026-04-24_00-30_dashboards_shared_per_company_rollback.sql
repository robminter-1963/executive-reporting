-- ============================================================================
-- Rollback — Phase 3 shared-per-company dashboards.
--
-- Restores the user_id column shape on RPT_master_dashboard_tabs and
-- RPT_master_dashboard_tiles. The PURGED ROWS FROM THE FORWARD MIGRATION
-- ARE NOT RECOVERED — this rollback only restores the schema; the admin
-- who ran the forward migration should restore a backup if the prior
-- rows matter.
--
-- On re-add: user_id is NOT NULL with a placeholder 'unknown' default so
-- the schema matches the pre-Phase-3 shape without blocking existing rows.
-- Callers should stop using the user_id column for reads before applying
-- this rollback in a production scenario.
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- ── 1. Drop the Phase-3-only indexes (shared-layout covering indexes) ─────
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_master_dashboard_tabs_company'
             AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs'))
BEGIN
    DROP INDEX IX_master_dashboard_tabs_company ON EMPOWER.RPT_master_dashboard_tabs;
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_master_dashboard_tiles_company_tab_shared'
             AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles'))
BEGIN
    DROP INDEX IX_master_dashboard_tiles_company_tab_shared ON EMPOWER.RPT_master_dashboard_tiles;
END
GO

-- ── 2. Re-add user_id columns ─────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs')
                 AND name = 'user_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tabs
        ADD user_id NVARCHAR(128) NOT NULL
            CONSTRAINT DF_master_dashboard_tabs_user_id DEFAULT 'unknown';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tiles')
                 AND name = 'user_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD user_id NVARCHAR(128) NOT NULL
            CONSTRAINT DF_master_dashboard_tiles_user_id DEFAULT 'unknown';
END
GO

-- ── 3. Restore the (company_id, user_id) covering index on tabs ───────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_master_dashboard_tabs_company_user'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_master_dashboard_tabs'))
BEGIN
    CREATE INDEX IX_master_dashboard_tabs_company_user
        ON EMPOWER.RPT_master_dashboard_tabs (company_id, user_id);
END
GO
