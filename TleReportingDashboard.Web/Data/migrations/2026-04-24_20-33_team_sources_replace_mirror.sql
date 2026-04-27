-- ============================================================================
-- Permissions — replace the team mirror with a per-connection SQL source.
--
-- Supersedes 2026-04-24_20-07_teams.sql. The earlier design mirrored
-- EMPOWER.U_SET_TEAMS/U_SET_TEAMMEMBERS into ConfigDB on admin-triggered
-- sync, but the LOS team layout varies per customer and the data changes
-- often enough that a snapshot is the wrong abstraction. New model:
--   * Admins write a custom SELECT per connection that returns the team
--     rows (with manager info). The SQL is stored in RPT_team_sources.
--   * The User editor's team picker runs those configured SELECTs live
--     against each source connection the user has access to — nothing
--     is copied into ConfigDB.
--   * RPT_user_teams stays (user → team assignments), but its FK to
--     RPT_teams is dropped because team_id is now a logical reference
--     into whatever the live SELECT returns.
--
-- Destructive: RPT_teams and RPT_team_members are dropped. Any rows
-- inserted by the short-lived sync feature are lost — there was no
-- canonical data there, just a replayable mirror of the source.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. Drop the user_teams FK so RPT_teams can be dropped ──────────────────
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = 'FK_RPT_user_teams_team'
             AND parent_object_id = OBJECT_ID('EMPOWER.RPT_user_teams'))
BEGIN
    ALTER TABLE EMPOWER.RPT_user_teams DROP CONSTRAINT FK_RPT_user_teams_team;
END
GO

-- ── 2. Drop the mirror tables (members first because of FK) ─────────────────
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_team_members')
BEGIN
    DROP TABLE EMPOWER.RPT_team_members;
END
GO

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_teams')
BEGIN
    DROP TABLE EMPOWER.RPT_teams;
END
GO

-- ── 3. Per-connection team SQL source ──────────────────────────────────────
-- The teams_sql column holds a full SELECT statement whose column list must
-- include: team_id, team_name, manager_ext_id, manager_name, team_type.
-- Free-form on purpose — customers' LOS schemas vary enough that a rigid
-- column mapping would lock some out. The service layer validates the
-- output shape when Preview is clicked.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_team_sources')
BEGIN
    CREATE TABLE EMPOWER.RPT_team_sources (
        connection_id    UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
                                          REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
        teams_sql        NVARCHAR(MAX)    NOT NULL,
        updated_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_by       NVARCHAR(256)    NULL
    );
END
GO
