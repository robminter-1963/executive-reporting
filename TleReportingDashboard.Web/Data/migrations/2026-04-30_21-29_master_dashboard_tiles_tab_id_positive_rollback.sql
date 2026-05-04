-- Rollback for 2026-04-30_21-29_master_dashboard_tiles_tab_id_positive.sql.
-- Drops the CHECK constraint. After rollback, tiles can again carry
-- tab_id <= 0 — typically only useful when re-running the cleanup that
-- the constraint blocked.

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_master_dashboard_tiles_tab_id_positive')
BEGIN
    ALTER TABLE EMPOWER.RPT_master_dashboard_tiles
        DROP CONSTRAINT CK_master_dashboard_tiles_tab_id_positive;
END
GO
