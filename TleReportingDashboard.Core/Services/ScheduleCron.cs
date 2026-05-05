using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Computes the next fire-time from a ReportSchedule by reading the rich pattern JSON.
// Returns null when the schedule is inactive, expired, or the pattern can't produce a
// future fire-time within a reasonable search window.
public static class ScheduleNextRun
{
    public static DateTime? Compute(ReportSchedule s, DateTime nowLocal)
    {
        if (!s.IsActive) return null;
        if (s.EndDate.HasValue && nowLocal.Date > s.EndDate.Value.Date) return null;

        var pattern = !string.IsNullOrWhiteSpace(s.SchedulePatternJson)
            ? SchedulePattern.FromJson(s.SchedulePatternJson)
            : InferPatternFromCron(s.CronExpression);

        if (pattern is null) return null;

        var timeOfDay = new TimeSpan(pattern.Hour, pattern.Minute, 0);
        var earliest = s.StartDate.HasValue && s.StartDate.Value > nowLocal
            ? s.StartDate.Value
            : nowLocal;

        DateTime? next = pattern.Type switch
        {
            "OneTime" => ComputeOneTime(s.StartDate ?? nowLocal.Date, timeOfDay, nowLocal),
            "Daily"   => ComputeDaily(pattern, timeOfDay, s.StartDate, earliest),
            "Weekly"  => ComputeWeekly(pattern, timeOfDay, s.StartDate, earliest),
            "Monthly" => ComputeMonthly(pattern, timeOfDay, earliest),
            _ => null
        };

        if (next.HasValue && s.EndDate.HasValue && next.Value.Date > s.EndDate.Value.Date)
            return null;
        return next;
    }

    private static DateTime? ComputeOneTime(DateTime start, TimeSpan time, DateTime now)
    {
        var dt = start.Date + time;
        return dt > now ? dt : null;
    }

    private static DateTime ComputeDaily(SchedulePattern p, TimeSpan time, DateTime? startDate, DateTime earliest)
    {
        var candidate = earliest.Date + time;
        if (candidate <= earliest) candidate = candidate.AddDays(1);

        if (p.Interval > 1 && startDate.HasValue)
        {
            while (((candidate.Date - startDate.Value.Date).Days % p.Interval) != 0)
                candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    private static DateTime? ComputeWeekly(SchedulePattern p, TimeSpan time, DateTime? startDate, DateTime earliest)
    {
        if (p.DaysOfWeek is null || p.DaysOfWeek.Count == 0) return null;
        for (int i = 0; i < 14 * Math.Max(1, p.Interval); i++)
        {
            var day = earliest.Date.AddDays(i);
            var candidate = day + time;
            if (candidate <= earliest) continue;
            if (!p.DaysOfWeek.Contains(day.DayOfWeek)) continue;

            if (p.Interval > 1 && startDate.HasValue)
            {
                var weeks = (int)((day - startDate.Value.Date).TotalDays / 7);
                if (weeks % p.Interval != 0) continue;
            }
            return candidate;
        }
        return null;
    }

    private static DateTime? ComputeMonthly(SchedulePattern p, TimeSpan time, DateTime earliest)
    {
        for (int m = 0; m < 14; m++)
        {
            var month = earliest.AddMonths(m);
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            var candidateDays = new List<int>();

            if (p.DaysOfMonth is { Count: > 0 })
            {
                foreach (var d in p.DaysOfMonth.Where(d => d >= 1 && d <= daysInMonth))
                    candidateDays.Add(d);
            }
            else if (!string.IsNullOrEmpty(p.MonthlyOrdinal) && p.MonthlyDayOfWeek.HasValue)
            {
                var d = ResolveOrdinalWeekday(month.Year, month.Month, p.MonthlyOrdinal, p.MonthlyDayOfWeek.Value, daysInMonth);
                if (d > 0) candidateDays.Add(d);
            }

            foreach (var day in candidateDays.OrderBy(x => x))
            {
                var dt = new DateTime(month.Year, month.Month, day) + time;
                if (dt > earliest) return dt;
            }
        }
        return null;
    }

    private static int ResolveOrdinalWeekday(int year, int month, string ordinal, DayOfWeek dow, int daysInMonth)
    {
        if (ordinal.Equals("Last", StringComparison.OrdinalIgnoreCase))
        {
            for (int d = daysInMonth; d >= 1; d--)
                if (new DateTime(year, month, d).DayOfWeek == dow) return d;
            return 0;
        }
        int n = ordinal switch
        {
            "First" => 1, "Second" => 2, "Third" => 3, "Fourth" => 4, _ => 1
        };
        int count = 0;
        for (int d = 1; d <= daysInMonth; d++)
        {
            if (new DateTime(year, month, d).DayOfWeek == dow && ++count == n) return d;
        }
        return 0;
    }

    // Best-effort fallback for legacy rows that only have a cron expression.
    // Handles the shapes we generate: "m h * * *" (daily), "m h * * 0,1,..." (weekly),
    // "m h 1,15 * *" (monthly day-of-month).
    private static SchedulePattern? InferPatternFromCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;
        if (!int.TryParse(parts[0], out var minute) || !int.TryParse(parts[1], out var hour)) return null;

        var p = new SchedulePattern { Hour = hour, Minute = minute, Interval = 1 };

        if (parts[4] != "*")
        {
            p.Type = "Weekly";
            foreach (var tok in parts[4].Split(','))
                if (int.TryParse(tok, out var dow)) p.DaysOfWeek.Add((DayOfWeek)dow);
        }
        else if (parts[2] != "*")
        {
            p.Type = "Monthly";
            foreach (var tok in parts[2].Split(','))
                if (int.TryParse(tok, out var dom)) p.DaysOfMonth.Add(dom);
        }
        else
        {
            p.Type = "Daily";
        }
        return p;
    }
}

// Projects a SchedulePattern to a 5-field cron expression the worker can consume.
// Complex patterns (ordinal-weekday monthly, interval > 1 that cron can't represent
// natively) collapse to a reasonable approximation; the worker should prefer the
// pattern JSON when available.
public static class ScheduleCron
{
    public static string Build(SchedulePattern p)
    {
        var min = p.Minute;
        var hr = p.Hour;

        return p.Type switch
        {
            "OneTime" => $"{min} {hr} * * *",  // one-time: worker checks date separately

            "Daily" => p.Interval > 1
                ? $"{min} {hr} */{p.Interval} * *"   // every N days (approximation)
                : $"{min} {hr} * * *",

            "Weekly" => p.DaysOfWeek is { Count: > 0 }
                ? $"{min} {hr} * * {string.Join(",", p.DaysOfWeek.Select(d => (int)d).OrderBy(i => i))}"
                : $"{min} {hr} * * *",

            "Monthly" when p.DaysOfMonth is { Count: > 0 }
                => $"{min} {hr} {string.Join(",", p.DaysOfMonth.OrderBy(d => d))} * *",

            "Monthly" when !string.IsNullOrEmpty(p.MonthlyOrdinal) && p.MonthlyDayOfWeek.HasValue
                // Cron has no native "first Monday" — emit any-day cron and let the worker
                // filter by ordinal when it runs. Safe since worker checks SchedulePatternJson.
                => $"{min} {hr} * * {(int)p.MonthlyDayOfWeek.Value}",

            _ => $"{min} {hr} * * *"  // fallback = daily
        };
    }

    // Human-readable summary for display in the schedules list.
    public static string Describe(SchedulePattern p)
    {
        var timeStr = new TimeSpan(p.Hour, p.Minute, 0);
        var time = DateTime.Today.Add(timeStr).ToString("h:mm tt");

        switch (p.Type)
        {
            case "OneTime":
                return $"Once at {time}";

            case "Daily":
                return p.Interval > 1
                    ? $"Every {p.Interval} days at {time}"
                    : $"Daily at {time}";

            case "Weekly":
                if (p.DaysOfWeek is null || p.DaysOfWeek.Count == 0)
                    return $"Weekly at {time}";
                var days = string.Join(", ", p.DaysOfWeek.OrderBy(d => (int)d).Select(d => d.ToString()[..3]));
                return p.Interval > 1
                    ? $"Every {p.Interval} weeks on {days} at {time}"
                    : $"Weekly on {days} at {time}";

            case "Monthly" when p.DaysOfMonth is { Count: > 0 }:
                var daysOm = string.Join(", ", p.DaysOfMonth.OrderBy(d => d));
                return $"Monthly on day {daysOm} at {time}";

            case "Monthly" when !string.IsNullOrEmpty(p.MonthlyOrdinal) && p.MonthlyDayOfWeek.HasValue:
                return $"Monthly on the {p.MonthlyOrdinal} {p.MonthlyDayOfWeek} at {time}";

            case "Monthly":
                return $"Monthly at {time}";

            default:
                return $"At {time}";
        }
    }
}
