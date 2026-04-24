-- Adds a per-user, per-report rows-per-page override map. JSON object keyed
-- by report id (string GUID) with the rows-per-page as the value.
--   { "7d4cd47d-0465-43b6-8979-8b67ad1f2a1a": 30,
--     "c2a8...": 50 }

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'report_page_sizes') IS NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD report_page_sizes NVARCHAR(MAX) NULL;
GO
