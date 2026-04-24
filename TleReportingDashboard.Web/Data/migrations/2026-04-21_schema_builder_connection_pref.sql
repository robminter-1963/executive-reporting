-- ============================================================================
-- Remember the admin's last-selected Schema Builder connection
--
-- Per-user, per-company (scoped by user_id since RPT_user_preferences is
-- keyed on the Entra user id). Null means "no preference" — the page falls
-- back to the company default on load. Persists across browsers/machines so
-- admins resume on whichever connection they were last editing.
-- ============================================================================

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'schema_builder_connection_id') IS NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD schema_builder_connection_id UNIQUEIDENTIFIER NULL;
GO
