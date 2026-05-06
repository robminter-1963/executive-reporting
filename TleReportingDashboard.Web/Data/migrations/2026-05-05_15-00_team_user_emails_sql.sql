-- Adds a third Team Builder SQL: user_emails_sql.
-- Resolves a team member's LOS login (member_ext_id) to an email
-- address. Used by the scheduled-report Worker on Individual
-- schedules so a team's roster can be turned into mailable addresses
-- without requiring every team member to also have an
-- RPT_user_connection_logins mapping.
--
-- Required output columns: member_ext_id, email.
-- The customer-supplied SELECT is responsible for joining whichever
-- LOS user table holds the email — that's why this is a third
-- pluggable query rather than an inline column on members_sql.
--
-- Idempotent — re-runs are no-ops.

IF COL_LENGTH('EMPOWER.RPT_team_sources', 'user_emails_sql') IS NULL
    ALTER TABLE EMPOWER.RPT_team_sources ADD user_emails_sql NVARCHAR(MAX) NULL;
GO
