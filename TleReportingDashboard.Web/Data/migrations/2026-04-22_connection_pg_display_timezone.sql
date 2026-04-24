-- ============================================================================
-- RPT_company_connections — add pg_display_timezone column
--
-- Postgres column that stores an IANA timezone name (e.g. 'America/Los_Angeles').
-- Used by the report-query emitter to wrap date/datetime field expressions
-- with `AT TIME ZONE '<tz>'` when the field carries ApplyTimezoneConversion = true.
--
-- Nullable because:
--   * Non-Postgres connections don't use it (ignored).
--   * Postgres connections that don't need display-time conversion can leave
--     it blank — no wrapping happens unless both the connection has a tz and
--     the field has the flag on.
--
-- Idempotent: safe to re-run. Paired with
-- 2026-04-22_connection_pg_display_timezone_rollback.sql.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_company_connections', 'pg_display_timezone') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_company_connections
        ADD pg_display_timezone NVARCHAR(100) NULL;
END
GO
