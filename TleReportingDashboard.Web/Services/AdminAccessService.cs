namespace TleReportingDashboard.Web.Services;

// Resolves "can this user access this admin section?" given an email and a
// section key from AdminSections.cs. Used by:
//   * MainLayout — to decide whether to show the "Admin" entry in the user
//     menu (visible if the user can access at least one section).
//   * Admin.razor — to gate each MudTabPanel.
//   * Schema Builder page — to gate the whole page.
//   * Each AdminXxxTab — defense-in-depth self-check.
//
// Resolution rules:
//   1. AdminService.IsAdmin(email) → true  ⇒ allowed for every section.
//   2. UserRecord.RoleAdminSections contains the key ⇒ allowed.
//   3. Otherwise denied.
//
// The class doesn't cache beyond what UserManagementService already does —
// every call hits GetByEmailAsync, which is cache-backed. Per-call cost
// is one cache hit in the steady state.
public interface IAdminAccessService
{
    Task<bool> CanAccessAsync(string sectionKey, string? email, CancellationToken ct = default);
    Task<bool> HasAnyAdminAccessAsync(string? email, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetAllowedSectionsAsync(string? email, CancellationToken ct = default);
}

public sealed class AdminAccessService : IAdminAccessService
{
    private readonly IUserManagementService _users;
    private readonly IAdminService _admins;

    public AdminAccessService(IUserManagementService users, IAdminService admins)
    {
        _users = users;
        _admins = admins;
    }

    public async Task<bool> CanAccessAsync(string sectionKey, string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sectionKey)) return false;
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (_admins.IsAdmin(email)) return true;
        var user = await _users.GetByEmailAsync(email, ct);
        return user?.RoleAdminSections is { Count: > 0 } sections
            && sections.Contains(sectionKey, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> HasAnyAdminAccessAsync(string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (_admins.IsAdmin(email)) return true;
        var user = await _users.GetByEmailAsync(email, ct);
        return user?.RoleAdminSections is { Count: > 0 };
    }

    public async Task<IReadOnlySet<string>> GetAllowedSectionsAsync(string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Administrator → every section.
        if (_admins.IsAdmin(email))
            return new HashSet<string>(AdminSections.All.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);

        var user = await _users.GetByEmailAsync(email, ct);
        if (user?.RoleAdminSections is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(user.RoleAdminSections, StringComparer.OrdinalIgnoreCase);
    }
}
