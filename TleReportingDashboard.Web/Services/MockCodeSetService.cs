namespace TleReportingDashboard.Web.Services;

public class MockCodeSetService : ICodeSetService
{
    public Task<List<CodeSetValue>> GetCodeSetValuesAsync(int codeSetId)
    {
        // Return sample values for mock mode
        var values = codeSetId switch
        {
            30 => new List<CodeSetValue>
            {
                new("Applied", "Applied"), new("Processing", "Processing"),
                new("Underwriting", "Underwriting"), new("Approved", "Approved"),
                new("Docs Out", "Docs Out"), new("Funded", "Funded"),
                new("Denied", "Denied"), new("Cancel", "Cancel")
            },
            1 => new List<CodeSetValue>
            {
                new("Conventional", "Conventional"), new("FHA", "FHA"),
                new("VA", "VA"), new("USDA", "USDA"), new("Jumbo", "Jumbo")
            },
            4 => new List<CodeSetValue>
            {
                new("Purchase", "Purchase"), new("Refinance", "Refinance"),
                new("Cash-Out Refinance", "Cash-Out Refinance")
            },
            _ => new List<CodeSetValue>()
        };
        return Task.FromResult(values);
    }
}
