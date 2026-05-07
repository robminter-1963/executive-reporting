using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// CRUD for the admin-curated Library Sections list. Sections are scoped
// per-company (each company manages its own list) so the Report Library's
// "All Reports" tab can render them as section headers above grouped rows.
public interface ILibrarySectionService
{
    // Lists active sections for the company in sort_order, then name. The
    // Report Builder dropdown and the Library section-header iteration
    // both use this — caching at the service layer keeps the call cheap
    // even when many tiles render in parallel.
    Task<List<LibrarySection>> GetSectionsAsync(Guid companyId);

    // Creates a section with the given name + sort order. Returns the
    // persisted record (id assigned). Duplicate name (case-insensitive)
    // within the same company surfaces as InvalidOperationException —
    // the unique filtered index in the migration enforces it; the
    // service catches the collision and rethrows with a friendlier
    // message for the Snackbar.
    Task<LibrarySection> CreateSectionAsync(Guid companyId, string name, int sortOrder);

    // Renames a section. Same dup-name protection as Create.
    Task RenameSectionAsync(Guid sectionId, string newName);

    // Reorder by passing the full ordered id list — service writes
    // sort_order = index for each. Persisted in a single transaction so
    // partial failures can't leave the list in mixed-order state.
    Task ReorderSectionsAsync(Guid companyId, IList<Guid> orderedIds);

    // Soft-delete (is_active = 0) so historical references on reports'
    // library_section_id stay intact for audit. The FK's ON DELETE SET
    // NULL handles hard deletes too — but we prefer soft-delete by
    // default so admins don't accidentally unlink dozens of reports.
    Task DeleteSectionAsync(Guid sectionId);
}
