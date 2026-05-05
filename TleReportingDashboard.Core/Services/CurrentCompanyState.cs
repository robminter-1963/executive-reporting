namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Scoped state container that holds the user's active company for the
/// lifetime of a Blazor circuit. Set by whichever page "activates" a company
/// (the master dashboard route, the Report Library picker, etc.) and read by
/// MainLayout for breadcrumb rendering, tile loaders, and anything else that
/// scopes to a single company per session.
/// </summary>
///
/// Distinct from <see cref="UserPreferenceState"/>: preferences persist across
/// sessions (saved to RPT_user_preferences), whereas the "current company" is
/// a transient UI concept that resets every circuit. The persisted
/// "last visited company" lives on RPT_users.last_visited_company_id and is
/// only read by the picker page to decide whether to auto-redirect.
public class CurrentCompanyState
{
    public Guid? Id { get; private set; }
    public string? Code { get; private set; }
    public string? Name { get; private set; }

    // The user's effective email inside the current company. Mirrors the
    // login (Entra) email when no per-company override exists; carries
    // the RPT_user_companies.email value when an admin has set a
    // company-specific contact address. Read by MainLayout for the
    // top-right user badge and by any consumer that wants the "as the
    // user appears in this company" address (scheduled deliveries,
    // share notifications). Auth identity is unaffected — this is
    // contact / display only.
    public string? ActiveEmail { get; private set; }

    public bool IsSet => Id is not null;

    public event Action? OnChange;

    public void Set(Guid id, string code, string name, string? activeEmail = null)
    {
        Id = id;
        Code = code;
        Name = name;
        ActiveEmail = activeEmail;
        OnChange?.Invoke();
    }

    public void Clear()
    {
        Id = null;
        Code = null;
        Name = null;
        ActiveEmail = null;
        OnChange?.Invoke();
    }
}
