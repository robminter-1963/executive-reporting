-- ============================================================================
-- Guards RPT_master_dashboard_tiles.tab_id from sliding back to 0 (or any
-- non-positive value). RPT_master_dashboard_tabs.id is INT IDENTITY(1,1) so
-- 0 is never a real tab — but a stray default / mis-applied UPDATE could
-- silently leave tiles orphaned (rendered nowhere, surfaced nowhere). The
-- CHECK turns that into a hard failure at write time instead of a phantom-
-- empty-tab render.
--
-- Idempotent: catalog-checked. Safe to re-run.
--
-- Pre-requisite: any existing tab_id = 0 rows must be cleaned up first or
-- the ALTER ADD CONSTRAINT will fail. Caller can verify with:
--   SELECT COUNT(*) FROM EMPOWER.RPT_master_dashboard_tiles WHERE tab_id <= 0;
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_master_dashboard_tiles_tab_id_positive')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        ADD CONSTRAINT CK_master_dashboard_tiles_tab_id_positive
            CHECK (tab_id > 0);
END
GO
