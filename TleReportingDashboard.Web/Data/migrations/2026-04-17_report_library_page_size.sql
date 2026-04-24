-- Adds report_library_page_size column used by ReportLibrary's MudTablePager.
-- Separate from default_page_size which is meant for the report data grid itself.

IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'report_library_page_size') IS NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        ADD report_library_page_size INT NOT NULL
            CONSTRAINT DF_user_preferences_report_library_page_size DEFAULT 15;
GO
