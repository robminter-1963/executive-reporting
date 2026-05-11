-- Rollback: 2026-05-09_16-00_user_favorites.sql

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES
           WHERE TABLE_SCHEMA = 'EMPOWER'
             AND TABLE_NAME = 'RPT_user_favorites')
BEGIN
    DROP TABLE EMPOWER.RPT_user_favorites;
END
GO
