using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Services;
using ConfigFieldDefinition = TleReportingDashboard.Web.Configuration.FieldDefinition;

namespace TleReportingDashboard.Tests.Services;

public class SchemaServiceTests
{
    private readonly SchemaService _service;

    public SchemaServiceTests()
    {
        var schemaConfig = new SchemaConfig
        {
            Fields =
            [
                new ConfigFieldDefinition { Id = "loan_number", Label = "Loan Number", Description = "The loan number", Domain = "Loan Details", DataType = "text", FieldType = "Dimension", SourceTable = "LOAN", SourceColumn = "LOANNUMBER" },
                new ConfigFieldDefinition { Id = "loan_amount", Label = "Loan Amount", Description = "The loan amount", Domain = "Loan Details", DataType = "currency", FieldType = "Measure", SourceTable = "LOAN", SourceColumn = "LOANAMOUNT" },
                new ConfigFieldDefinition { Id = "borrower_name", Label = "Borrower Name", Description = "The borrower name", Domain = "Borrower", DataType = "text", FieldType = "Dimension", SourceTable = "BORROWER", SourceColumn = "BORROWERNAME" },
                new ConfigFieldDefinition { Id = "credit_score", Label = "Credit Score", Description = "The credit score", Domain = "Borrower", DataType = "integer", FieldType = "Measure", SourceTable = "BORROWER", SourceColumn = "CREDITSCORE", RolesRequired = "Dashboard.Compliance" },
                new ConfigFieldDefinition { Id = "property_address", Label = "Property Address", Description = "The property address", Domain = "Property", DataType = "text", FieldType = "Dimension", SourceTable = "PROPERTY", SourceColumn = "PROPERTYADDRESS" }
            ],
            Joins =
            [
                new JoinDefinition { Id = "loan_borrower", Sql = "INNER JOIN BORROWER ON LOAN.LOANID = BORROWER.LOANID" },
                new JoinDefinition { Id = "loan_property", Sql = "INNER JOIN PROPERTY ON LOAN.LOANID = PROPERTY.LOANID" }
            ]
        };

        var store = new Mock<ISchemaConfigStore>();
        store.Setup(s => s.Current).Returns(schemaConfig);

        var codeSetService = new Mock<ICodeSetService>();
        codeSetService.Setup(c => c.GetCodeSetValuesAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CodeSetValue>());

        var logger = new Mock<ILogger<SchemaService>>();
        var connectionAdmin = new Mock<ICompanyConnectionAdminService>();
        _service = new SchemaService(store.Object, codeSetService.Object, connectionAdmin.Object, logger.Object);
    }

    [Fact]
    public async Task GetDomainGroupsAsync_ReturnsFieldsGroupedByDomain()
    {
        // Act
        var result = await _service.GetDomainGroupsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(g => g.Name == "Loan Details");
        result.Should().Contain(g => g.Name == "Borrower");
        result.Should().Contain(g => g.Name == "Property");

        var loanDetails = result.First(g => g.Name == "Loan Details");
        loanDetails.Fields.Should().HaveCount(2);
        loanDetails.Fields[0].Id.Should().Be("loan_number");
        loanDetails.Fields[1].Id.Should().Be("loan_amount");
    }

    [Fact]
    public async Task GetFieldConfigsAsync_ReturnsAllFields()
    {
        // Act
        var result = await _service.GetFieldConfigsAsync();

        // Assert
        result.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetDomainGroupsAsync_ReturnsAllFieldsIncludingRestrictedWhenRoleProvided()
    {
        // Act - the new config-based service returns all fields with their
        // RolesRequired metadata; the caller is responsible for filtering.
        var result = await _service.GetDomainGroupsAsync("Dashboard.User");

        // Assert - all fields present; credit_score included with its RolesRequired metadata
        var borrowerGroup = result.FirstOrDefault(g => g.Name == "Borrower");
        borrowerGroup.Should().NotBeNull();
        borrowerGroup!.Fields.Should().Contain(f => f.Id == "borrower_name");
        borrowerGroup.Fields.Should().Contain(f => f.Id == "credit_score");
        borrowerGroup.Fields.First(f => f.Id == "credit_score").RolesRequired.Should().Be("Dashboard.Compliance");
    }

    [Fact]
    public async Task GetDomainGroupsAsync_ReturnsAllFieldsWhenNoRoleFilter()
    {
        // Act
        var result = await _service.GetDomainGroupsAsync();

        // Assert - all fields should be present including restricted ones
        var allFields = result.SelectMany(g => g.Fields).ToList();
        allFields.Should().HaveCount(5);
        allFields.Should().Contain(f => f.Id == "credit_score");
    }

    [Fact]
    public async Task GetDomainGroupsAsync_ReturnsRestrictedFieldsForMatchingRole()
    {
        // Act
        var result = await _service.GetDomainGroupsAsync("Dashboard.Compliance");

        // Assert
        var allFields = result.SelectMany(g => g.Fields).ToList();
        allFields.Should().Contain(f => f.Id == "credit_score");
    }

    [Fact]
    public async Task GetJoinConfigsAsync_ReturnsAllJoinConfigs()
    {
        // Act
        var result = await _service.GetJoinConfigsAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(j => j.ToTable == "BORROWER");
        result.Should().Contain(j => j.ToTable == "PROPERTY");
    }
}
