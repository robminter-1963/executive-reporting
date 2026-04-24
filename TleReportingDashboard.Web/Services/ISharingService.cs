using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

public interface ISharingService
{
    Task<List<ReportShare>> GetSharesForReportAsync(Guid reportId);
    Task<List<SavedReport>> GetSharedWithMeAsync(string userId);
    Task<ReportShare> ShareReportAsync(ReportShare share);
    Task RevokeShareAsync(Guid shareId, string requesterId);
}
