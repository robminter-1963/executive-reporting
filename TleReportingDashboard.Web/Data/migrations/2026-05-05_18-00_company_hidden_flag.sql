-- Adds is_hidden flag to RPT_companies. Independent of is_active:
--   * is_active controls whether the company is functional. Inactive
--     rows are blocked from queries / navigation entirely.
--   * is_hidden controls whether the company's tile renders on the
--     all-companies picker (`/?all=1`). A hidden-but-active company
--     stays fully usable via direct nav (/master-dashboard/{code})
--     and via dropdowns elsewhere — it just doesn't appear on the
--     visual picker grid.
--
-- The two flags overlap intentionally: a deactivated company is also
-- hidden by virtue of the picker's existing `is_active = 1` filter;
-- this column adds a "still active but please don't show me on the
-- grid" intermediate state for parked / WIP / archived companies the
-- admin doesn't want to clutter the front door with.
--
-- Idempotent — re-runs are no-ops.

IF COL_LENGTH('EMPOWER.RPT_companies', 'is_hidden') IS NULL
    ALTER TABLE EMPOWER.RPT_companies
        ADD is_hidden BIT NOT NULL CONSTRAINT DF_RPT_companies_is_hidden DEFAULT 0;
GO
