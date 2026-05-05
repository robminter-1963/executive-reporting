using System.Text.Json;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Mock implementation of ISchemaService, IQueryService, IReportService, ISharingService, and IScheduleService
/// for development/demo purposes. Contains 34 fields, 50 loans, demo reports, and templates.
/// </summary>
public class MockDataService : ISchemaService, IQueryService, IReportService, ISharingService, IScheduleService
{
    private readonly List<FieldConfig> _fieldConfigs;
    private readonly List<JoinConfig> _joinConfigs;
    private readonly List<Dictionary<string, object?>> _mockLoans;
    private readonly List<SavedReport> _savedReports;
    private readonly List<ReportShare> _reportShares = new();
    private readonly List<ReportSchedule> _reportSchedules = new();
    private readonly object _reportLock = new();
    private readonly object _shareLock = new();
    private readonly object _scheduleLock = new();

    public MockDataService()
    {
        _fieldConfigs = BuildFieldConfigs();
        _joinConfigs = BuildJoinConfigs();
        _mockLoans = BuildMockLoans();
        _savedReports = BuildDemoReports();
    }

    #region ISchemaService

    public Task<List<DomainGroup>> GetDomainGroupsAsync(string? userRole = null, Guid? connectionId = null)
    {
        var filteredFields = _fieldConfigs.Where(f =>
        {
            if (f.RolesRequired is null)
                return true;
            if (userRole is null)
                return true;
            return f.RolesRequired == userRole;
        }).ToList();

        var domainGroups = filteredFields
            .GroupBy(f => f.Domain)
            .Select(g => new DomainGroup
            {
                Name = g.Key,
                Fields = g.OrderBy(f => f.SortOrder)
                    .Select(f => new FieldDefinition
                    {
                        Id = f.Id,
                        Label = f.Label,
                        DataType = f.DataType,
                        Description = f.Description,
                        FieldType = f.FieldType,
                        RolesRequired = f.RolesRequired,
                        DefaultRedactionValue = f.DefaultRedactionValue,
                        SourceTable = f.SourceTable,
                        SourceColumn = f.SourceColumn
                    })
                    .ToList()
            })
            .OrderBy(g => g.Name switch
            {
                "Loan Details" => 0,
                "Borrower" => 1,
                "Property" => 2,
                "Dates & Milestones" => 3,
                "Team & Pipeline" => 4,
                _ => 99
            })
            .ToList();

        return Task.FromResult(domainGroups);
    }

    public Task<List<FieldConfig>> GetFieldConfigsAsync(Guid? connectionId = null)
    {
        return Task.FromResult(_fieldConfigs.ToList());
    }

    public Task<List<JoinConfig>> GetJoinConfigsAsync(Guid? connectionId = null)
    {
        return Task.FromResult(_joinConfigs.ToList());
    }

    public Task<List<Configuration.LookupDefinition>> GetLookupsAsync(Guid? connectionId = null)
    {
        return Task.FromResult(new List<Configuration.LookupDefinition>());
    }

    public Task<List<Configuration.CustomFilterDefinition>> GetCustomFiltersAsync(Guid? connectionId = null)
    {
        return Task.FromResult(new List<Configuration.CustomFilterDefinition>());
    }

    #endregion

    #region IQueryService

    public Task<(string Sql, Dictionary<string, object?> Parameters)> BuildSqlAsync(QueryRequest request)
    {
        // Mock mode: no real SQL pipeline. Return a placeholder so the debug
        // dialog has something to render when called.
        return Task.FromResult(
            ("/* mock mode — no SQL is executed against a real database */",
             new Dictionary<string, object?>()));
    }

    public Task<QueryResponse> ExecuteQueryAsync(QueryRequest request)
    {
        var fieldLookup = _fieldConfigs.ToDictionary(f => f.Id, f => f);
        var dedupedFieldIds = request.FieldIds.Distinct().ToList();

        // Start with all mock loans
        var filtered = _mockLoans.AsEnumerable();

        // Apply filters
        if (request.Filters != null)
        {
            foreach (var filter in request.Filters)
            {
                if (filter.Value is null) continue;
                var fieldId = filter.Key;
                var filterValue = filter.Value.ToString();

                filtered = filtered.Where(row =>
                    row.ContainsKey(fieldId) &&
                    row[fieldId] != null &&
                    string.Equals(row[fieldId]!.ToString(), filterValue, StringComparison.OrdinalIgnoreCase));
            }
        }

        var filteredList = filtered.ToList();
        var totalCount = filteredList.Count;

        // Apply sorting
        if (!string.IsNullOrEmpty(request.SortField) && fieldLookup.TryGetValue(request.SortField, out var sortField))
        {
            var isDescending = string.Equals(request.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);

            filteredList = isDescending
                ? filteredList.OrderByDescending(r => GetSortableValue(r, request.SortField, sortField.DataType)).ToList()
                : filteredList.OrderBy(r => GetSortableValue(r, request.SortField, sortField.DataType)).ToList();
        }

        // Apply pagination
        var pageSize = Math.Min(request.PageSize, QueryRequest.MaxPageSize);
        if (pageSize <= 0) pageSize = 50;
        var page = Math.Max(request.Page, 1);
        var offset = (page - 1) * pageSize;

        var pagedRows = filteredList
            .Skip(offset)
            .Take(pageSize)
            .Select(row =>
            {
                // Only include requested fields
                var projectedRow = new Dictionary<string, object?>();
                foreach (var fieldId in dedupedFieldIds)
                {
                    projectedRow[fieldId] = row.ContainsKey(fieldId) ? row[fieldId] : null;
                }
                return projectedRow;
            })
            .ToList();

        // Build columns metadata
        var columns = dedupedFieldIds
            .Where(id => fieldLookup.ContainsKey(id))
            .Select(id => new ColumnMeta
            {
                FieldId = id,
                Label = fieldLookup[id].Label,
                DataType = fieldLookup[id].DataType,
                MaxLength = fieldLookup[id].MaxLength,
                ValueSortOrder = fieldLookup[id].ValueSortOrder,
                Format = fieldLookup[id].Format
            })
            .ToList();

        return Task.FromResult(new QueryResponse
        {
            Columns = columns,
            Rows = pagedRows,
            TotalCount = totalCount
        });
    }

    private static object? GetSortableValue(Dictionary<string, object?> row, string fieldId, string dataType)
    {
        if (!row.ContainsKey(fieldId) || row[fieldId] is null)
            return null;

        var value = row[fieldId];
        return dataType switch
        {
            "currency" or "percent" => Convert.ToDecimal(value),
            "integer" => Convert.ToInt32(value),
            "date" => value is DateTime dt ? dt : DateTime.Parse(value!.ToString()!),
            _ => value?.ToString()
        };
    }

    #endregion

    #region IReportService

    public Task<List<SavedReport>> GetReportsAsync(string userId)
    {
        lock (_reportLock)
        {
            var reports = _savedReports.Where(r => r.OwnerId == userId).ToList();
            return Task.FromResult(reports);
        }
    }

    public Task<List<SavedReport>> GetAllReportsAsync()
    {
        lock (_reportLock)
        {
            return Task.FromResult(_savedReports.ToList());
        }
    }

    public Task<SavedReport?> GetReportByIdAsync(Guid id)
    {
        lock (_reportLock)
        {
            return Task.FromResult<SavedReport?>(_savedReports.FirstOrDefault(r => r.Id == id));
        }
    }

    public Task<SavedReport> SaveReportAsync(SavedReport report)
    {
        lock (_reportLock)
        {
            report.Id = Guid.NewGuid();
            report.CreatedAt = DateTime.UtcNow;
            report.UpdatedAt = DateTime.UtcNow;
            _savedReports.Add(report);
            return Task.FromResult(report);
        }
    }

    public Task<SavedReport> UpdateReportAsync(SavedReport report)
    {
        lock (_reportLock)
        {
            var existing = _savedReports.FirstOrDefault(r => r.Id == report.Id);
            if (existing is null) throw new KeyNotFoundException($"Report {report.Id} not found.");
            if (existing.OwnerId != report.OwnerId) throw new UnauthorizedAccessException();

            existing.Name = report.Name;
            existing.FieldIds = report.FieldIds;
            existing.Filters = report.Filters;
            existing.Aggregations = report.Aggregations;
            existing.ColumnState = report.ColumnState;
            existing.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(existing);
        }
    }

    public Task DeleteReportAsync(Guid id, string userId)
    {
        lock (_reportLock)
        {
            var existing = _savedReports.FirstOrDefault(r => r.Id == id);
            if (existing is null) throw new KeyNotFoundException($"Report {id} not found.");
            if (existing.OwnerId != userId) throw new UnauthorizedAccessException();

            _savedReports.Remove(existing);
            return Task.CompletedTask;
        }
    }

    public Task<List<string>> GetDistinctCategoriesAsync()
    {
        lock (_reportLock)
        {
            var cats = _savedReports
                .Where(r => !string.IsNullOrWhiteSpace(r.Category))
                .Select(r => r.Category!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(cats);
        }
    }

    public Task<List<ReportSchedule>> GetAllSchedulesAsync() =>
        Task.FromResult(new List<ReportSchedule>());

    #endregion

    #region ISharingService

    public Task<List<ReportShare>> GetSharesForReportAsync(Guid reportId)
    {
        lock (_shareLock)
        {
            var shares = _reportShares.Where(s => s.ReportId == reportId).ToList();
            return Task.FromResult(shares);
        }
    }

    public Task<List<SavedReport>> GetSharedWithMeAsync(string userId)
    {
        lock (_shareLock)
        {
            var sharedReportIds = _reportShares
                .Where(s => s.SharedWithId == userId)
                .Select(s => s.ReportId)
                .Distinct()
                .ToHashSet();

            lock (_reportLock)
            {
                var reports = _savedReports.Where(r => sharedReportIds.Contains(r.Id)).ToList();
                return Task.FromResult(reports);
            }
        }
    }

    public Task<ReportShare> ShareReportAsync(ReportShare share)
    {
        lock (_shareLock)
        {
            var existing = _reportShares.FirstOrDefault(s =>
                s.ReportId == share.ReportId && s.SharedWithId == share.SharedWithId);

            if (existing is not null)
            {
                existing.Permission = share.Permission;
                return Task.FromResult(existing);
            }

            share.Id = Guid.NewGuid();
            share.CreatedAt = DateTime.UtcNow;
            _reportShares.Add(share);
            return Task.FromResult(share);
        }
    }

    public Task RevokeShareAsync(Guid shareId, string requesterId)
    {
        lock (_shareLock)
        {
            var share = _reportShares.FirstOrDefault(s => s.Id == shareId);
            if (share is null) throw new InvalidOperationException($"Share '{shareId}' not found.");
            if (share.SharedById != requesterId) throw new UnauthorizedAccessException("Only the share creator can revoke a share.");

            _reportShares.Remove(share);
            return Task.CompletedTask;
        }
    }

    #endregion

    #region IScheduleService

    public Task<List<ReportSchedule>> GetSchedulesForReportAsync(Guid reportId)
    {
        lock (_scheduleLock)
        {
            var schedules = _reportSchedules.Where(s => s.ReportId == reportId).ToList();
            return Task.FromResult(schedules);
        }
    }

    public Task<List<ReportSchedule>> GetSchedulesForUserAsync(string userId)
    {
        lock (_scheduleLock)
        {
            var schedules = _reportSchedules.Where(s => s.OwnerId == userId).ToList();
            return Task.FromResult(schedules);
        }
    }

    public Task<ReportSchedule> CreateScheduleAsync(ReportSchedule schedule)
    {
        lock (_scheduleLock)
        {
            schedule.Id = Guid.NewGuid();
            schedule.CreatedAt = DateTime.UtcNow;
            schedule.IsActive = true;
            schedule.ConsecutiveFailures = 0;
            _reportSchedules.Add(schedule);
            return Task.FromResult(schedule);
        }
    }

    public Task<ReportSchedule> UpdateScheduleAsync(ReportSchedule schedule)
    {
        lock (_scheduleLock)
        {
            var existing = _reportSchedules.FirstOrDefault(s => s.Id == schedule.Id);
            if (existing is null) throw new InvalidOperationException($"Schedule '{schedule.Id}' not found.");
            if (existing.OwnerId != schedule.OwnerId) throw new UnauthorizedAccessException("Only the schedule owner can update a schedule.");

            existing.CronExpression = schedule.CronExpression;
            existing.Subject = schedule.Subject;
            existing.AttachmentFormat = schedule.AttachmentFormat;
            existing.IncludePreview = schedule.IncludePreview;
            existing.IsActive = schedule.IsActive;
            return Task.FromResult(existing);
        }
    }

    public Task DeactivateScheduleAsync(Guid scheduleId, string userId)
    {
        lock (_scheduleLock)
        {
            var schedule = _reportSchedules.FirstOrDefault(s => s.Id == scheduleId);
            if (schedule is null) throw new InvalidOperationException($"Schedule '{scheduleId}' not found.");
            if (schedule.OwnerId != userId) throw new UnauthorizedAccessException("Only the schedule owner can deactivate a schedule.");

            schedule.IsActive = false;
            return Task.CompletedTask;
        }
    }

    public Task DeleteScheduleAsync(Guid scheduleId, string userId)
    {
        lock (_scheduleLock)
        {
            var schedule = _reportSchedules.FirstOrDefault(s => s.Id == scheduleId);
            if (schedule is null) throw new InvalidOperationException($"Schedule '{scheduleId}' not found.");
            if (schedule.OwnerId != userId) throw new UnauthorizedAccessException("Only the schedule owner can delete a schedule.");

            _reportSchedules.Remove(schedule);
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Data Builders

    private static List<FieldConfig> BuildFieldConfigs()
    {
        return new List<FieldConfig>
        {
            // Loan Details (10 fields)
            new() { Id = "loan_number", Label = "Loan Number", Description = "Unique loan identifier", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANNUMBER", SortOrder = 1 },
            new() { Id = "loan_amount", Label = "Loan Amount", Description = "Original loan amount", FieldType = "Measure", Domain = "Loan Details", DataType = "currency", SourceTable = "LOAN", SourceColumn = "LOANAMOUNT", SortOrder = 2 },
            new() { Id = "loan_type", Label = "Loan Type", Description = "Type of loan (Conventional, FHA, VA, etc.)", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANTYPE", SortOrder = 3 },
            new() { Id = "loan_purpose", Label = "Loan Purpose", Description = "Purpose of the loan (Purchase, Refinance, etc.)", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANPURPOSE", SortOrder = 4 },
            new() { Id = "loan_status", Label = "Loan Status", Description = "Current status of the loan", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANSTATUS", SortOrder = 5 },
            new() { Id = "interest_rate", Label = "Interest Rate", Description = "Note interest rate", FieldType = "Measure", Domain = "Loan Details", DataType = "percent", SourceTable = "LOAN", SourceColumn = "INTERESTRATE", SortOrder = 6 },
            new() { Id = "ltv", Label = "LTV", Description = "Loan-to-value ratio", FieldType = "Measure", Domain = "Loan Details", DataType = "percent", SourceTable = "LOAN", SourceColumn = "LTV", SortOrder = 7 },
            new() { Id = "dti", Label = "DTI", Description = "Debt-to-income ratio", FieldType = "Measure", Domain = "Loan Details", DataType = "percent", SourceTable = "LOAN", SourceColumn = "DTI", SortOrder = 8 },
            new() { Id = "loan_program", Label = "Loan Program", Description = "Loan program name", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANPROGRAM", SortOrder = 9 },
            new() { Id = "channel", Label = "Channel", Description = "Origination channel", FieldType = "Dimension", Domain = "Loan Details", DataType = "text", SourceTable = "LOAN", SourceColumn = "CHANNEL", SortOrder = 10 },

            // Borrower (4 fields)
            new() { Id = "borrower_name", Label = "Borrower Name", Description = "Full name of the primary borrower", FieldType = "Dimension", Domain = "Borrower", DataType = "text", SourceTable = "BORROWER", SourceColumn = "BORROWERNAME", SortOrder = 1 },
            new() { Id = "credit_score", Label = "Credit Score", Description = "Borrower credit score", FieldType = "Measure", Domain = "Borrower", DataType = "integer", SourceTable = "BORROWER", SourceColumn = "CREDITSCORE", SortOrder = 2, RolesRequired = "Dashboard.Compliance", DefaultRedactionValue = "***" },
            new() { Id = "monthly_income", Label = "Monthly Income", Description = "Borrower monthly income", FieldType = "Measure", Domain = "Borrower", DataType = "currency", SourceTable = "BORROWER", SourceColumn = "MONTHLYINCOME", SortOrder = 3 },
            new() { Id = "employment_status", Label = "Employment Status", Description = "Employment status of the borrower", FieldType = "Dimension", Domain = "Borrower", DataType = "text", SourceTable = "BORROWER", SourceColumn = "EMPLOYMENTSTATUS", SortOrder = 4 },

            // Property (7 fields)
            new() { Id = "property_address", Label = "Property Address", Description = "Street address of the property", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "PROPERTYADDRESS", SortOrder = 1 },
            new() { Id = "property_city", Label = "Property City", Description = "City of the property", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "PROPERTYCITY", SortOrder = 2 },
            new() { Id = "property_state", Label = "Property State", Description = "State of the property", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "PROPERTYSTATE", SortOrder = 3 },
            new() { Id = "property_zip", Label = "Property Zip", Description = "ZIP code of the property", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "PROPERTYZIP", SortOrder = 4 },
            new() { Id = "property_type", Label = "Property Type", Description = "Type of property (Single Family, Condo, etc.)", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "PROPERTYTYPE", SortOrder = 5 },
            new() { Id = "property_value", Label = "Property Value", Description = "Appraised value of the property", FieldType = "Measure", Domain = "Property", DataType = "currency", SourceTable = "PROPERTY", SourceColumn = "PROPERTYVALUE", SortOrder = 6 },
            new() { Id = "occupancy_type", Label = "Occupancy Type", Description = "Occupancy type of the property", FieldType = "Dimension", Domain = "Property", DataType = "text", SourceTable = "PROPERTY", SourceColumn = "OCCUPANCYTYPE", SortOrder = 7 },

            // Dates & Milestones (6 fields)
            new() { Id = "application_date", Label = "Application Date", Description = "Date the loan application was submitted", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "APPLICATIONDATE", SortOrder = 1 },
            new() { Id = "approval_date", Label = "Approval Date", Description = "Date the loan was approved", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "APPROVALDATE", SortOrder = 2 },
            new() { Id = "closing_date", Label = "Closing Date", Description = "Date the loan was closed", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "CLOSINGDATE", SortOrder = 3 },
            new() { Id = "funding_date", Label = "Funding Date", Description = "Date the loan was funded", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "FUNDINGDATE", SortOrder = 4 },
            new() { Id = "lock_date", Label = "Lock Date", Description = "Date the interest rate was locked", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "LOCKDATE", SortOrder = 5 },
            new() { Id = "lock_expiration", Label = "Lock Expiration", Description = "Date the rate lock expires", FieldType = "Dimension", Domain = "Dates & Milestones", DataType = "date", SourceTable = "LOANMILESTONES", SourceColumn = "LOCKEXPIRATION", SortOrder = 6 },

            // Team & Pipeline (7 fields)
            new() { Id = "loan_officer", Label = "Loan Officer", Description = "Assigned loan officer", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "LOANOFFICER", SortOrder = 1 },
            new() { Id = "branch", Label = "Branch", Description = "Originating branch office", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "BRANCH", SortOrder = 2 },
            new() { Id = "processor", Label = "Processor", Description = "Assigned loan processor", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "PROCESSOR", SortOrder = 3 },
            new() { Id = "underwriter", Label = "Underwriter", Description = "Assigned underwriter", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "UNDERWRITER", SortOrder = 4 },
            new() { Id = "pipeline_stage", Label = "Pipeline Stage", Description = "Current pipeline stage", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "PIPELINESTAGE", SortOrder = 5 },
            new() { Id = "investor_name", Label = "Investor Name", Description = "Target investor for the loan", FieldType = "Dimension", Domain = "Team & Pipeline", DataType = "text", SourceTable = "LOAN", SourceColumn = "INVESTORNAME", SortOrder = 6 },
            new() { Id = "days_in_stage", Label = "Days in Stage", Description = "Number of days in current pipeline stage", FieldType = "Measure", Domain = "Team & Pipeline", DataType = "integer", SourceTable = "LOAN", SourceColumn = "DAYSINSTAGE", SortOrder = 7 },
        };
    }

    private static List<JoinConfig> BuildJoinConfigs()
    {
        return new List<JoinConfig>
        {
            new() { Id = 1, FromTable = "LOAN", FromColumn = "LOANID", ToTable = "BORROWER", ToColumn = "LOANID", JoinType = "INNER JOIN" },
            new() { Id = 2, FromTable = "LOAN", FromColumn = "LOANID", ToTable = "PROPERTY", ToColumn = "LOANID", JoinType = "INNER JOIN" },
            new() { Id = 3, FromTable = "LOAN", FromColumn = "LOANID", ToTable = "LOANMILESTONES", ToColumn = "LOANID", JoinType = "LEFT JOIN" },
        };
    }

    private static List<Dictionary<string, object?>> BuildMockLoans()
    {
        var random = new Random(42); // fixed seed for reproducibility
        var loans = new List<Dictionary<string, object?>>();

        var loanTypes = new[] { "Conventional", "FHA", "VA", "USDA", "Jumbo" };
        var loanPurposes = new[] { "Purchase", "Refinance", "Cash-Out Refinance" };
        var loanStatuses = new[] { "Active", "Funded", "Closed", "Suspended", "Denied" };
        var loanPrograms = new[] { "30-Year Fixed", "15-Year Fixed", "5/1 ARM", "7/1 ARM", "30-Year FHA" };
        var channels = new[] { "Retail", "Wholesale", "Correspondent" };
        var employmentStatuses = new[] { "Employed", "Self-Employed", "Retired" };
        var propertyTypes = new[] { "Single Family", "Condo", "Townhouse", "Multi-Family" };
        var occupancyTypes = new[] { "Primary Residence", "Second Home", "Investment" };
        var pipelineStages = new[] { "Application", "Processing", "Underwriting", "Approved", "Clear to Close", "Funded" };
        var states = new[] { "CA", "TX", "FL", "NY", "WA", "AZ", "CO", "GA", "NC", "OH" };
        var cities = new[] { "Los Angeles", "Houston", "Miami", "New York", "Seattle", "Phoenix", "Denver", "Atlanta", "Charlotte", "Columbus" };
        var officers = new[] { "Sarah Johnson", "Mike Chen", "Lisa Park", "David Wilson", "Amy Roberts" };
        var branches = new[] { "Downtown", "Westside", "Northgate", "Southpark", "Eastview" };
        var processors = new[] { "Tom Brown", "Jane Smith", "Bob Miller", "Sue Davis", "Pat Lee" };
        var underwriters = new[] { "Chris Adams", "Kim Nguyen", "Mark Taylor", "Anna White", "Joe Clark" };
        var investors = new[] { "Fannie Mae", "Freddie Mac", "Ginnie Mae", "Wells Fargo", "Chase" };

        var firstNames = new[] { "James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda", "William", "Elizabeth",
            "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen",
            "Christopher", "Lisa", "Daniel", "Nancy", "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra",
            "Donald", "Ashley", "Steven", "Dorothy", "Paul", "Kimberly", "Andrew", "Emily", "Joshua", "Donna",
            "Kenneth", "Michelle", "Kevin", "Carol", "Brian", "Amanda", "George", "Melissa", "Timothy", "Deborah" };
        var lastNames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin",
            "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
            "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores",
            "Green", "Adams", "Nelson", "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts" };

        for (int i = 0; i < 50; i++)
        {
            var loanAmount = Math.Round((decimal)(185000 + random.NextDouble() * 640000), 2);
            var propertyValue = Math.Round(loanAmount * (decimal)(1.1 + random.NextDouble() * 0.4), 2);
            var ltv = Math.Round(loanAmount / propertyValue * 100, 2);
            var interestRate = Math.Round((decimal)(3.5 + random.NextDouble() * 4.0), 3);
            var creditScore = 620 + random.Next(181);
            var monthlyIncome = Math.Round((decimal)(4000 + random.NextDouble() * 12000), 2);
            var dti = Math.Round(loanAmount * interestRate / 100 / 12 / monthlyIncome * 100, 2);

            var applicationDate = new DateTime(2025, 1, 1).AddDays(random.Next(365));
            var approvalDate = applicationDate.AddDays(7 + random.Next(21));
            var closingDate = approvalDate.AddDays(14 + random.Next(30));
            var fundingDate = closingDate.AddDays(1 + random.Next(5));
            var lockDate = applicationDate.AddDays(3 + random.Next(10));
            var lockExpiration = lockDate.AddDays(30 + random.Next(60));

            var stateIndex = random.Next(states.Length);
            var daysInStage = random.Next(1, 45);

            loans.Add(new Dictionary<string, object?>
            {
                ["loan_number"] = $"LN-2025-{(i + 1):D5}",
                ["loan_amount"] = loanAmount,
                ["loan_type"] = loanTypes[random.Next(loanTypes.Length)],
                ["loan_purpose"] = loanPurposes[random.Next(loanPurposes.Length)],
                ["loan_status"] = loanStatuses[random.Next(loanStatuses.Length)],
                ["interest_rate"] = interestRate,
                ["ltv"] = ltv,
                ["dti"] = dti,
                ["loan_program"] = loanPrograms[random.Next(loanPrograms.Length)],
                ["channel"] = channels[random.Next(channels.Length)],
                ["borrower_name"] = $"{firstNames[i]} {lastNames[i]}",
                ["credit_score"] = creditScore,
                ["monthly_income"] = monthlyIncome,
                ["employment_status"] = employmentStatuses[random.Next(employmentStatuses.Length)],
                ["property_address"] = $"{100 + random.Next(9900)} {lastNames[random.Next(lastNames.Length)]} St",
                ["property_city"] = cities[stateIndex],
                ["property_state"] = states[stateIndex],
                ["property_zip"] = $"{10000 + random.Next(90000)}",
                ["property_type"] = propertyTypes[random.Next(propertyTypes.Length)],
                ["property_value"] = propertyValue,
                ["occupancy_type"] = occupancyTypes[random.Next(occupancyTypes.Length)],
                ["application_date"] = applicationDate,
                ["approval_date"] = approvalDate,
                ["closing_date"] = closingDate,
                ["funding_date"] = fundingDate,
                ["lock_date"] = lockDate,
                ["lock_expiration"] = lockExpiration,
                ["loan_officer"] = officers[random.Next(officers.Length)],
                ["branch"] = branches[random.Next(branches.Length)],
                ["processor"] = processors[random.Next(processors.Length)],
                ["underwriter"] = underwriters[random.Next(underwriters.Length)],
                ["pipeline_stage"] = pipelineStages[random.Next(pipelineStages.Length)],
                ["investor_name"] = investors[random.Next(investors.Length)],
                ["days_in_stage"] = daysInStage,
            });
        }

        return loans;
    }

    private static List<SavedReport> BuildDemoReports()
    {
        return new List<SavedReport>
        {
            new()
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Pipeline Summary",
                OwnerId = "demo-user",
                OwnerEmail = "dev.user@tle.com",
                FieldIds = JsonSerializer.Serialize(new List<string> { "loan_number", "loan_amount", "loan_status", "pipeline_stage", "loan_officer", "days_in_stage" }),
                Filters = null,
                Aggregations = null,
                ColumnState = null,
                LastRunAt = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                UpdatedAt = DateTime.UtcNow.AddDays(-1)
            },
            new()
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Funded by Officer",
                OwnerId = "demo-user",
                OwnerEmail = "dev.user@tle.com",
                FieldIds = JsonSerializer.Serialize(new List<string> { "loan_officer", "loan_amount", "loan_number", "borrower_name", "funding_date" }),
                Filters = JsonSerializer.Serialize(new Dictionary<string, object?> { { "loan_status", "Funded" } }),
                Aggregations = null,
                ColumnState = null,
                LastRunAt = DateTime.UtcNow.AddDays(-3),
                CreatedAt = DateTime.UtcNow.AddDays(-20),
                UpdatedAt = DateTime.UtcNow.AddDays(-3)
            },
            new()
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Name = "Branch Performance",
                OwnerId = "demo-user",
                OwnerEmail = "dev.user@tle.com",
                FieldIds = JsonSerializer.Serialize(new List<string> { "branch", "loan_amount", "loan_number", "loan_status", "channel" }),
                Filters = null,
                Aggregations = null,
                ColumnState = null,
                LastRunAt = null,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                UpdatedAt = DateTime.UtcNow.AddDays(-5)
            }
        };
    }

    #endregion
}
