IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RPT_user_notifications' AND SCHEMA_NAME(schema_id) = 'EMPOWER')
BEGIN
    DROP TABLE EMPOWER.RPT_user_notifications;
END
GO
