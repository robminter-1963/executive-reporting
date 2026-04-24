using FluentAssertions;
using Microsoft.Data.SqlClient;
using TleReportingDashboard.Web.Models;
using TleReportingDashboard.Web.Services;

namespace TleReportingDashboard.Tests.Services;

public class QueryBuilderTests
{
    private readonly List<FieldConfig> _fieldConfigs;
    private readonly List<JoinConfig> _joinConfigs;

    public QueryBuilderTests()
    {
        _fieldConfigs = new List<FieldConfig>
        {
            new() { Id = "loan_number", Label = "Loan Number", Domain = "Loan Details", DataType = "text", SourceTable = "EMPOWER.LN_MTGTERMS", SourceColumn = "LOAN_NUM", SortOrder = 1 },
            new() { Id = "loan_amount", Label = "Loan Amount", Domain = "Loan Details", DataType = "currency", SourceTable = "EMPOWER.LN_MTGTERMS", SourceColumn = "LOANAMT", SortOrder = 2 },
            new() { Id = "loan_status", Label = "Loan Status", Domain = "Loan Details", DataType = "text", SourceTable = "EMPOWER.LN_CODES", SourceColumn = "CODEDESC", SortOrder = 3 },
            new() { Id = "borrower_name", Label = "Borrower Name", Domain = "Borrower", DataType = "text", SourceTable = "EMPOWER.LN_BORRINFO", SourceColumn = "BORR_NAME", SortOrder = 1 },
            new() { Id = "credit_score", Label = "Credit Score", Domain = "Borrower", DataType = "integer", SourceTable = "EMPOWER.LN_BORRINFO", SourceColumn = "CREDIT_SCORE", SortOrder = 2 },
            new() { Id = "property_address", Label = "Property Address", Domain = "Property", DataType = "text", SourceTable = "EMPOWER.LN_PROPINFO", SourceColumn = "PROP_ADDR", SortOrder = 1 },
            new() { Id = "closing_date", Label = "Closing Date", Domain = "Dates & Milestones", DataType = "date", SourceTable = "EMPOWER.LN_MTGTERMS", SourceColumn = "EXPCLOSEDATE", SortOrder = 1 },
        };

        _joinConfigs = new List<JoinConfig>
        {
            new() { Id = 1, FromTable = "EMPOWER.LN_MTGTERMS", FromColumn = "LNKEY", ToTable = "EMPOWER.LN_BORRINFO", ToColumn = "LNKEY", JoinType = "INNER JOIN" },
            new() { Id = 2, FromTable = "EMPOWER.LN_MTGTERMS", FromColumn = "LNKEY", ToTable = "EMPOWER.LN_PROPINFO", ToColumn = "LNKEY", JoinType = "INNER JOIN" },
            new() { Id = 3, FromTable = "EMPOWER.LN_MTGTERMS", FromColumn = "LNKEY", ToTable = "EMPOWER.LN_CODES", ToColumn = "LNKEY", JoinType = "LEFT JOIN" },
        };
    }

    [Fact]
    public void RejectsRequestWithUnknownFieldId()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "unknown_field" }
        };

        // Act
        var act = () => QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*unknown_field*");
    }

    [Fact]
    public void GeneratesSqlWithOnlyWhitelistedColumns()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        sql.Should().Contain("EMPOWER.LN_MTGTERMS.LOAN_NUM");
        sql.Should().Contain("EMPOWER.LN_MTGTERMS.LOANAMT");
        sql.Should().NotContain("unknown");
    }

    [Fact]
    public void AllFilterValuesBecomeParameters()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            Filters = new Dictionary<string, object?>
            {
                { "loan_status", "Active" },
                { "loan_amount", 500000m }
            }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert — 2 filter params + 2 pagination params (@_offset, @_pageSize)
        parameters.Should().Contain(p => p.ParameterName == "@filter_loan_status");
        parameters.Should().Contain(p => p.ParameterName == "@filter_loan_amount");
        sql.Should().NotContain("'Active'");
        sql.Should().NotContain("500000");
        // Verify filter values are parameterized, not interpolated
        var filterParams = parameters.Where(p => p.ParameterName.StartsWith("@filter_")).ToList();
        filterParams.Should().HaveCount(2);
    }

    [Fact]
    public void PaginationCappedAt500Rows()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number" },
            PageSize = 1000,
            Page = 1
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert — pagination is parameterized, verify the parameter value is capped
        parameters.Should().Contain(p => p.ParameterName == "@_pageSize" && (int)p.Value! == 1000);
    }

    [Fact]
    public void EmptyFieldListThrowsArgumentException()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string>()
        };

        // Act
        var act = () => QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*field*");
    }

    [Fact]
    public void DuplicateFieldIdsAreDeduplicatedInOutput()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_number", "loan_amount" }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        // Count occurrences of LOAN.LOANNUMBER in SELECT portion (before FROM)
        var selectClause = sql.Substring(0, sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase));
        var occurrences = selectClause.Split("EMPOWER.LN_MTGTERMS.LOAN_NUM").Length - 1;
        occurrences.Should().Be(1, "duplicate field IDs should be deduplicated");
    }

    [Fact]
    public void GeneratesCorrectJoinsWhenFieldsSpanMultipleTables()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "borrower_name", "property_address" }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        sql.Should().Contain("INNER JOIN EMPOWER.LN_BORRINFO");
        sql.Should().Contain("INNER JOIN EMPOWER.LN_PROPINFO");
    }

    [Fact]
    public void SingleTableQueryHasNoJoins()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        sql.Should().NotContain("JOIN");
    }

    [Fact]
    public void SortFieldIsValidatedAndIncludedInOrderBy()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            SortField = "loan_amount",
            SortDirection = "desc"
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("EMPOWER.LN_MTGTERMS.LOANAMT");
        sql.Should().Contain("DESC");
    }

    [Fact]
    public void FilterOnFieldNotInConfigThrowsArgumentException()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number" },
            Filters = new Dictionary<string, object?>
            {
                { "nonexistent_field", "value" }
            }
        };

        // Act
        var act = () => QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*nonexistent_field*");
    }

    [Fact]
    public void PaginationOffsetIsCorrectForPageTwo()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number" },
            Page = 2,
            PageSize = 50
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert — pagination is now parameterized
        sql.Should().Contain("OFFSET @_offset ROWS");
        sql.Should().Contain("FETCH NEXT @_pageSize ROWS ONLY");
        parameters.Should().Contain(p => p.ParameterName == "@_offset" && (int)p.Value! == 50);
        parameters.Should().Contain(p => p.ParameterName == "@_pageSize" && (int)p.Value! == 50);
    }

    [Fact]
    public void LeftJoinIsUsedWhenConfigured()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_status" }
        };

        // Act
        var (sql, parameters) = QueryBuilder.BuildQuery(request, _fieldConfigs, _joinConfigs);

        // Assert
        sql.Should().Contain("LEFT JOIN EMPOWER.LN_CODES");
    }
}
