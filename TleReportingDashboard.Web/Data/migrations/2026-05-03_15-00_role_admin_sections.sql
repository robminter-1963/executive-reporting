-- ============================================================================
-- Adds RPT_roles.admin_sections — a JSON array of admin-section keys the role
-- is allowed to access. Lets non-Administrator roles (e.g. "System Support")
-- get tab-level access to the Admin page without being elevated to full admin.
--
-- Conventions:
--   * NULL or empty array = role has NO admin access. The Admin nav item is
--     hidden in MainLayout and the /admin page denies access.
--   * Administrator role is hard-coded to bypass this column entirely (always
--     sees every tab); no check needed against the JSON.
--   * Section keys are short snake_case identifiers — see AdminSections.cs
--     for the catalog. Unknown keys are ignored, not rejected, so future
--     section additions don't break older roles.
--
-- Idempotent: COL_LENGTH guards the ALTER. Paired with the rollback file.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'EMPOWER')
    EXEC('CREATE SCHEMA EMPOWER');
GO

IF COL_LENGTH('EMPOWER.RPT_roles', 'admin_sections') IS NULL
BEGIN
    ALTER TABLE EMPOWER.RPT_roles
        ADD admin_sections NVARCHAR(MAX) NULL;
END
GO
