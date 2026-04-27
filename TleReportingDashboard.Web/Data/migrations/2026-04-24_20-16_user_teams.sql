-- ============================================================================
-- Permissions — per-user team assignments.
--
-- Many-to-many join between RPT_users (via user_id or the email stub — same
-- convention as RPT_user_companies and RPT_user_connection_logins) and
-- RPT_teams. Populated from the User editor when the user's role scope is
-- 'team'. Rows are kept dormant if scope changes to 'all'/'self' so a later
-- toggle back doesn't lose the prior picks.
--
-- The QueryBuilder team-scope predicate (future work) will join this table
-- with RPT_team_members on (connection_id, team_id) to resolve the set of
-- member_ext_id values that should pass the row filter.
--
-- Idempotent: safe to re-run.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
               WHERE TABLE_SCHEMA = 'EMPOWER' AND TABLE_NAME = 'RPT_user_teams')
BEGIN
    CREATE TABLE EMPOWER.RPT_user_teams (
        user_id          NVARCHAR(128)    NOT NULL,
        connection_id    UNIQUEIDENTIFIER NOT NULL,
        team_id          INT              NOT NULL,
        created_at       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_RPT_user_teams PRIMARY KEY (user_id, connection_id, team_id),
        CONSTRAINT FK_RPT_user_teams_team FOREIGN KEY (connection_id, team_id)
            REFERENCES EMPOWER.RPT_teams(connection_id, team_id) ON DELETE CASCADE
    );

    -- Covering index for "what teams does this user belong to" — the QueryBuilder
    -- predicate hits this direction, not the per-team "who's in this team" one.
    CREATE INDEX IX_RPT_user_teams_user ON EMPOWER.RPT_user_teams (user_id);
END
GO
