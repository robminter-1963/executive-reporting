namespace TleReportingDashboard.Web.Services;

public interface IEmailService
{
    Task SendReportEmailAsync(string recipientEmail, string subject, string htmlBody,
        byte[]? attachment, string attachmentFileName);
}
