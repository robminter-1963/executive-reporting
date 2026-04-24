using FluentAssertions;
using TleReportingDashboard.Web.Models;
using TleReportingDashboard.Web.Services;

namespace TleReportingDashboard.Tests.Services;

public class MockDataServiceTests
{
    private readonly MockDataService _service;

    public MockDataServiceTests()
    {
        _service = new MockDataService();
    }

    [Fact]
    public async Task GetDomainGroupsAsync_Returns34FieldsAcross5Domains()
    {
        // Act
        var result = await _service.GetDomainGroupsAsync();

        // Assert
        result.Should().HaveCount(5);
        result.Select(g => g.Name).Should().BeEquivalentTo(
            "Loan Details", "Borrower", "Property", "Dates & Milestones", "Team & Pipeline");

        var totalFields = result.SelectMany(g => g.Fields).Count();
        totalFields.Should().Be(34);
    }

    [Fact]
    public async Task ExecuteQueryAsync_Returns50MockLoans()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            PageSize = 100
        };

        // Act
        var result = await _service.ExecuteQueryAsync(request);

        // Assert
        result.TotalCount.Should().Be(50);
        result.Rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task ExecuteQueryAsync_FilteringByTextField_NarrowsResults()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_status" },
            Filters = new Dictionary<string, object?> { { "loan_status", "Active" } },
            PageSize = 100
        };

        // Act
        var result = await _service.ExecuteQueryAsync(request);

        // Assert
        result.TotalCount.Should().BeLessThan(50);
        result.TotalCount.Should().BeGreaterThan(0);
        result.Rows.Should().OnlyContain(r => r["loan_status"]!.ToString() == "Active");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SortingByDifferentFieldTypes_Works()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            SortField = "loan_amount",
            SortDirection = "asc",
            PageSize = 100
        };

        // Act
        var result = await _service.ExecuteQueryAsync(request);

        // Assert
        result.Rows.Should().HaveCountGreaterThan(1);
        var amounts = result.Rows.Select(r => Convert.ToDecimal(r["loan_amount"])).ToList();
        amounts.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ExecuteQueryAsync_PaginationReturnsCorrectSubset()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _service.ExecuteQueryAsync(request);

        // Assert
        result.Rows.Should().HaveCount(10);
        result.TotalCount.Should().Be(50);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SecondPageReturnsCorrectSubset()
    {
        // Arrange
        var requestPage1 = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number" },
            Page = 1,
            PageSize = 10
        };
        var requestPage2 = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number" },
            Page = 2,
            PageSize = 10
        };

        // Act
        var resultPage1 = await _service.ExecuteQueryAsync(requestPage1);
        var resultPage2 = await _service.ExecuteQueryAsync(requestPage2);

        // Assert
        resultPage1.Rows.Should().HaveCount(10);
        resultPage2.Rows.Should().HaveCount(10);
        var page1Ids = resultPage1.Rows.Select(r => r["loan_number"]).ToList();
        var page2Ids = resultPage2.Rows.Select(r => r["loan_number"]).ToList();
        page1Ids.Should().NotIntersectWith(page2Ids);
    }

    [Fact]
    public async Task GetReportsAsync_Returns3DemoSavedReports()
    {
        // Arrange
        var demoUserId = "demo-user";

        // Act
        var result = await _service.GetReportsAsync(demoUserId);

        // Assert
        result.Should().HaveCount(3);
        result.Select(r => r.Name).Should().BeEquivalentTo(
            "Pipeline Summary", "Funded by Officer", "Branch Performance");
    }

    [Fact]
    public async Task GetFieldConfigsAsync_Returns34Fields()
    {
        // Act
        var result = await _service.GetFieldConfigsAsync();

        // Assert
        result.Should().HaveCount(34);
    }

    [Fact]
    public async Task GetJoinConfigsAsync_ReturnsJoinConfigs()
    {
        // Act
        var result = await _service.GetJoinConfigsAsync();

        // Assert
        result.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExecuteQueryAsync_DescendingSortWorks()
    {
        // Arrange
        var request = new QueryRequest
        {
            FieldIds = new List<string> { "loan_number", "loan_amount" },
            SortField = "loan_amount",
            SortDirection = "desc",
            PageSize = 100
        };

        // Act
        var result = await _service.ExecuteQueryAsync(request);

        // Assert
        var amounts = result.Rows.Select(r => Convert.ToDecimal(r["loan_amount"])).ToList();
        amounts.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task GetDomainGroupsAsync_FiltersFieldsByRole()
    {
        // Act - requesting with a non-matching role should exclude restricted fields
        var result = await _service.GetDomainGroupsAsync("Dashboard.User");

        // Assert
        var allFields = result.SelectMany(g => g.Fields).ToList();
        // Fields with RolesRequired set should be excluded when role doesn't match
        allFields.Should().NotContain(f => f.Id == "credit_score");
    }

}
