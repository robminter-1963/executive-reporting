namespace TleReportingDashboard.Web.Services;

// In-app notifications inbox. Producers (sharing, scheduled-report runs,
// admin announcements) call CreateAsync; the bell-icon UI in MainLayout
// reads + marks-read.
//
// Email-keyed (not user_id) so pre-provisioned users who haven't signed
// in yet still accumulate a backlog they'll see on first sign-in. The
// service is intentionally agnostic about who can produce what — gating
// belongs at the call site.
public interface INotificationService
{
    Task CreateAsync(
        string userEmail,
        string kind,
        string title,
        string? body = null,
        string? linkUrl = null,
        string? relatedEntityType = null,
        string? relatedEntityId = null,
        CancellationToken ct = default);

    Task<List<NotificationRecord>> GetForUserAsync(
        string userEmail,
        int max = 50,
        bool unreadOnly = false,
        CancellationToken ct = default);

    Task<int> GetUnreadCountAsync(string userEmail, CancellationToken ct = default);

    Task MarkReadAsync(Guid id, CancellationToken ct = default);

    Task MarkAllReadAsync(string userEmail, CancellationToken ct = default);

    // Remove a single notification permanently. Used by the per-row
    // X button on the bell dropdown.
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Bulk delete with optional filters. Both filters can combine:
    //   olderThanUtc = X    → only rows with created_at < X
    //   readOnly = true     → only rows with is_read = 1
    //   olderThanUtc = null + readOnly = false → wipes the whole inbox
    // Returns the row count actually deleted so the caller can show
    // a meaningful snackbar ("Cleared 12 notifications.").
    Task<int> DeleteRangeAsync(
        string userEmail,
        DateTime? olderThanUtc = null,
        bool readOnly = false,
        CancellationToken ct = default);
}

public sealed record NotificationRecord(
    Guid Id,
    string UserEmail,
    string Kind,
    string Title,
    string? Body,
    string? LinkUrl,
    string? RelatedEntityType,
    string? RelatedEntityId,
    bool IsRead,
    DateTime CreatedAt);

// Canonical kind discriminators. Free-text in the DB so producers can
// add new kinds without a schema change — this list documents the ones
// the UI's icon-picker recognizes; unknown kinds get a generic icon.
public static class NotificationKinds
{
    public const string ReportShared    = "report_shared";
    public const string ScheduleRan     = "schedule_ran";
    public const string ScheduleFailed  = "schedule_failed";
    public const string AdminAnnouncement = "admin_announcement";
}
