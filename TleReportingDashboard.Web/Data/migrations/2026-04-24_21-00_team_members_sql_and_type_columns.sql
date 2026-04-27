-- ============================================================================
-- Permissions — full admin-configurable team scope.
--
-- Extends the team-source config so nothing about the LOS schema is
-- hardcoded. Two pieces:
--
--   1. RPT_team_sources.members_sql — free-form SELECT returning
--      (team_id, member_ext_id). The query pipeline wraps this as a
--      subquery in the team-scope EXISTS predicate. Customers on a
--      non-Empower schema (or with custom membership tables) point the
--      SELECT wherever their membership lives; aliasing the output
--      columns to team_id / member_ext_id is the only contract.
--
--   2. RPT_team_type_columns — per (connection, team_type) mapping to
--      the owner column on the primary table. Example:
--         connection X, team_type='Processor' → PROCESSOR_USERID
--         connection X, team_type='Agent'     → LOAN_OFFICER_USERID
--      The pipeline emits one EXISTS per user team, using the team's
--      team_type to pick which primary-table column to filter on.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_team_sources.members_sql ────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('EMPOWER.RPT_team_sources')
                 AND name = 'members_sql')
BEGIN
    ALTER TABLE EMPOWER.RPT_team_sources
        ADD members_sql NVARCHAR(MAX) NULL;
END
GO

-- ── 2. RPT_team_type_columns ───────────────────────────────────────────────
-- owner_column is a raw column identifier on the primary table (e.g.
-- 'PROCESSOR_USERID'). The emitter qualifies it with the primary table's
-- alias at query time. One row per (connection_id, team_type) — a type
-- without a row is treated as unmapped and fails the scope check closed.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_team_type_columns')
BEGIN
    CREATE TABLE EMPOWER.RPT_team_type_columns (
        connection_id    UNIQUEIDENTIFIER NOT NULL
                                          REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
        team_type        NVARCHAR(20)     NOT NULL,
        owner_column     NVARCHAR(128)    NOT NULL,
        updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_RPT_team_type_columns PRIMARY KEY (connection_id, team_type)
    );
END
GO
