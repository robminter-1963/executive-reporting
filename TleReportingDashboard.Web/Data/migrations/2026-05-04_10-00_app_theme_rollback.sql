IF EXISTS (SELECT 1 FROM sys.tables
           WHERE name = 'RPT_app_theme'
             AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    DROP TABLE EMPOWER.RPT_app_theme;
END
GO
