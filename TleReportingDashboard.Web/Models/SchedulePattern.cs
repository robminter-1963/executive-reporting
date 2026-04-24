using System.Text.Json;

namespace TleReportingDashboard.Web.Models;

// Rich Windows-Task-Scheduler-style trigger. Stored as JSON on ReportSchedule.
public class SchedulePattern
{
    // "OneTime" | "Daily" | "Weekly" | "Monthly"
    public string Type { get; set; } = "Daily";

    // "Recur every N ___" — days for Daily, weeks for Weekly, months for Monthly.
    public int Interval { get; set; } = 1;

    // Weekly only — multi-select. Days schedule fires on.
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    // Monthly (day-of-month variant) — e.g. [1, 15] = 1st and 15th of month.
    public List<int> DaysOfMonth { get; set; } = new();

    // Monthly (ordinal-weekday variant) — e.g. ordinal=First, dayOfWeek=Monday.
    // Mutually exclusive with DaysOfMonth.
    public string? MonthlyOrdinal { get; set; }  // "First" | "Second" | "Third" | "Fourth" | "Last"
    public DayOfWeek? MonthlyDayOfWeek { get; set; }

    // Time-of-day the trigger fires, local wall-clock.
    public int Hour { get; set; } = 8;
    public int Minute { get; set; } = 0;

    public static SchedulePattern FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SchedulePattern();
        try
        {
            return JsonSerializer.Deserialize<SchedulePattern>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new SchedulePattern();
        }
        catch
        {
            return new SchedulePattern();
        }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
}
