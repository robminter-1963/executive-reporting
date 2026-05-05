using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Models;

namespace TleReportingDashboard.Web.Services;

// Schema resolver. Each method takes an optional connection id so the
// caller can target a specific connection's schema. When null the method
// uses ISchemaConfigStore.Current, which resolves to the TLE primary
// connection (back-compat for call sites that haven't been updated yet).
public interface ISchemaService
{
    Task<List<DomainGroup>> GetDomainGroupsAsync(string? userRole = null, Guid? connectionId = null);
    Task<List<FieldConfig>> GetFieldConfigsAsync(Guid? connectionId = null);
    Task<List<JoinConfig>> GetJoinConfigsAsync(Guid? connectionId = null);
    Task<List<CustomFilterDefinition>> GetCustomFiltersAsync(Guid? connectionId = null);
    Task<List<LookupDefinition>> GetLookupsAsync(Guid? connectionId = null);
}
