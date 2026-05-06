namespace TleReportingDashboard.Web.Models;

// Schedule targeting modes. Individual fans out one personalized,
// scope-resolved copy per Team Builder team member. Distribution
// sends a single email to a shared mailbox / dist-list address with
// no per-recipient scope (the saved report's own filters apply).
public enum ScheduleKind
{
    Distribution = 0,
    Individual = 1
}

public class ReportSchedule
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;

    // Targeting mode — drives which of (TeamId/TeamConnectionId) or
    // DistEmail the Worker honors. New rows default to Distribution
    // matching the migration default; legacy rows with no DistEmail
    // set fall through to OwnerEmail in the Worker so existing
    // schedules keep firing through the rollout window.
    public ScheduleKind Kind { get; set; } = ScheduleKind.Distribution;

    // Individual mode — both required when Kind == Individual. The
    // connection is required because Team Builder configuration
    // (teams_sql / members_sql / type→column map) lives per
    // connection, and team_id is opaque outside that context.
    public int? TeamId { get; set; }
    public Guid? TeamConnectionId { get; set; }

    // Distribution mode — required when Kind == Distribution and
    // the schedule isn't relying on the legacy owner-email fallback.
    public string? DistEmail { get; set; }

    // Cron is still the representation the worker consumes (best-effort).
    // The authoritative schedule source is SchedulePatternJson.
    public string CronExpression { get; set; } = string.Empty;

    // JSON-serialized SchedulePattern — rich trigger (type, interval, days,
    // ordinal-weekday, etc.). When present, the worker should honor this over
    // CronExpression. See ScheduleCron.BuildCron for the cron projection.
    public string? SchedulePatternJson { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public string Subject { get; set; } = string.Empty;

    // Deprecated — superseded by Kind + TeamId/TeamConnectionId/DistEmail.
    // Preserved on the model for the rollout window so a Worker rolled back
    // to the previous build can still read schedules persisted by the new
    // ReportDbService (which leaves these columns alone). New schedules
    // saved through the new dialog write null. Drop after the kind cutover
    // bakes in production.
    public string? Recipients { get; set; }
    public string? CcRecipients { get; set; }
    public string? BccRecipients { get; set; }

    public string AttachmentFormat { get; set; } = "xlsx";
    public bool IncludePreview { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? HangfireJobId { get; set; }
    public DateTime CreatedAt { get; set; }
}
