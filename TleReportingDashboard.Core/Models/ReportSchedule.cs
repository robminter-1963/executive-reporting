namespace TleReportingDashboard.Web.Models;

public class ReportSchedule
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;

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

    // Semicolon-separated email lists. Owner's email is always included in TO
    // by the worker even if this is empty (GLBA: owner audit trail).
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
