IF COL_LENGTH('EMPOWER.RPT_user_preferences', 'schema_builder_connection_id') IS NOT NULL
    ALTER TABLE EMPOWER.RPT_user_preferences
        DROP COLUMN schema_builder_connection_id;
GO
