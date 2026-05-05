namespace TleReportingDashboard.Web.Services;

public record CodeSetValue(string Code, string Description);

public interface ICodeSetService
{
    Task<List<CodeSetValue>> GetCodeSetValuesAsync(int codeSetId);
}
