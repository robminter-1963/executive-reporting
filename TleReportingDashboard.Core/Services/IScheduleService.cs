using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface IScheduleService
{
    Task<List<ReportSchedule>> GetSchedulesForReportAsync(Guid reportId);
    Task<List<ReportSchedule>> GetSchedulesForUserAsync(string userId);

    // Diagnostic — returns every schedule row. Used only for troubleshooting
    // owner_id mismatches; do not expose to non-admin users.
    Task<List<ReportSchedule>> GetAllSchedulesAsync();
    Task<ReportSchedule> CreateScheduleAsync(ReportSchedule schedule);
    Task<ReportSchedule> UpdateScheduleAsync(ReportSchedule schedule);
    Task DeactivateScheduleAsync(Guid scheduleId, string userId);
    Task DeleteScheduleAsync(Guid scheduleId, string userId);
}
