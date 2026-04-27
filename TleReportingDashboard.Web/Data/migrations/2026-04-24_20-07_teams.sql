-- ============================================================================
-- Permissions — team-scope foundation.
--
-- Mirror of the source LOS team tables (EMPOWER.U_SET_TEAMS and
-- EMPOWER.U_SET_TEAMMEMBERS on the admin-chosen connection) into ConfigDB.
-- Admins pick a connection in the new Admin → Team Builder tab, preview the
-- source rows, and run a full-replace sync. Teams live per-connection: the
-- same TEAMID can exist across different LOS environments without colliding.
--
-- TEAM_MEMBERID is the LOS login ID (the same value the QueryBuilder will
-- eventually compare against owner_col for the 'team' scope predicate), so
-- it's indexed here to keep that lookup cheap when it arrives.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

-- ── 1. RPT_teams ────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_teams')
BEGIN
    CREATE TABLE EMPOWER.RPT_teams (
        connection_id      UNIQUEIDENTIFIER NOT NULL
                                            REFERENCES EMPOWER.RPT_company_connections(id) ON DELETE CASCADE,
        team_id            INT              NOT NULL,
        team_name          NVARCHAR(100)    NULL,
        manager_ext_id     NVARCHAR(40)     NULL,
        manager_name       NVARCHAR(100)    NULL,
        team_type          NVARCHAR(20)     NULL,
        imported_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_RPT_teams PRIMARY KEY (connection_id, team_id)
    );
END
GO

-- ── 2. RPT_team_members ─────────────────────────────────────────────────────
-- NOTE: connection_id is denormalized onto RPT_team_members so the member
-- lookup for the team-scope predicate (WHERE connection_id = @c AND
-- member_ext_id = @login) hits a covering index without joining back to
-- RPT_teams. The (connection_id, team_id) FK keeps members tied to a
-- real team row on that connection.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_team_members')
BEGIN
    CREATE TABLE EMPOWER.RPT_team_members (
        connection_id      UNIQUEIDENTIFIER NOT NULL,
        member_id          INT              NOT NULL,
        team_id            INT              NOT NULL,
        member_ext_id      NVARCHAR(40)     NULL,
        member_name        NVARCHAR(100)    NULL,
        imported_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_RPT_team_members PRIMARY KEY (connection_id, member_id),
        CONSTRAINT FK_RPT_team_members_team FOREIGN KEY (connection_id, team_id)
            REFERENCES EMPOWER.RPT_teams(connection_id, team_id) ON DELETE CASCADE
    );
END
GO

-- Lookup index for the future team-scope predicate:
--   WHERE connection_id = @c AND member_ext_id = @login
-- Included columns let the resolver return team_id without a key lookup.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_RPT_team_members_connection_login'
                 AND object_id = OBJECT_ID('EMPOWER.RPT_team_members'))
BEGIN
    CREATE INDEX IX_RPT_team_members_connection_login
        ON EMPOWER.RPT_team_members (connection_id, member_ext_id)
        INCLUDE (team_id, member_name);
END
GO
