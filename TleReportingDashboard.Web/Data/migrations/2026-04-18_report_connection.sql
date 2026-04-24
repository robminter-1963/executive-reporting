-- ============================================================================
-- Add connection_id to RPT_saved_reports
--
-- Every report now explicitly names the RPT_company_connections row to use
-- at query time. "Default" becomes a UI hint for new reports only, not a
-- runtime fallback. Master dashboard tiles inherit the connection from the
-- report they point at (no new column on tiles).
--
-- Backfill strategy: on existing reports, point connection_id at the
-- company's is_default=1 connection. Fall back to the first active row if
-- no default is marked.
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- ── 1. Add the column (NULL for backfill) ────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_saved_reports')
                 AND name = 'connection_id')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD connection_id UNIQUEIDENTIFIER NULL;
END
GO

-- ── 2. Backfill: each report → its company's default/first-active connection ──
UPDATE r
SET r.connection_id = c.id
FROM EMPOWER.RPT_saved_reports r
CROSS APPLY (
    SELECT TOP 1 id
    FROM EMPOWER.RPT_company_connections
    WHERE company_id = r.company_id AND is_active = 1
    ORDER BY is_default DESC, name
) c
WHERE r.connection_id IS NULL;
GO

-- ── 3. FK + index (guarded against re-add) ──────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_saved_reports_connection')
BEGIN
    ALTER TABLE EMPOWER.RPT_saved_reports
        ADD CONSTRAINT FK_saved_reports_connection
        FOREIGN KEY (connection_id) REFERENCES EMPOWER.RPT_company_connections(id);
    -- ON DELETE NO ACTION: deleting a connection is blocked when any report
    -- still references it. Admins must reassign reports first.
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_saved_reports_connection'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_saved_reports'))
BEGIN
    CREATE INDEX IX_saved_reports_connection
        ON EMPOWER.RPT_saved_reports (connection_id);
END
GO

-- NOTE: connection_id is intentionally left NULLABLE. The runtime treats a
-- null as "use the company's is_default connection" for the transition.
-- Once every active report is backfilled, a follow-up migration can
-- ALTER COLUMN to NOT NULL. Keeping it nullable for now avoids breaking
-- reports that belong to companies with no connections yet (not our case
-- for TLE, but future companies might be in the middle of setup).
