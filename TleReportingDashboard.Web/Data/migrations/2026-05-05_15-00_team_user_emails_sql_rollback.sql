-- Rollback for 2026-05-05_15-00_team_user_emails_sql.sql.
-- Drops user_emails_sql from RPT_team_sources. Worker falls back to
-- the GetByExternalUserIdAsync chain (RPT_user_connection_logins) for
-- ext_id → email resolution, so dropping this column doesn't break
-- existing Individual schedules — they just go through the older,
-- mapping-based path.

IF COL_LENGTH('EMPOWER.RPT_team_sources', 'user_emails_sql') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_team_sources DROP COLUMN user_emails_sql;
GO
