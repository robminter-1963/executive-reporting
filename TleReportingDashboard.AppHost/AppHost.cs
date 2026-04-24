using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Only wire ConfigDb if configured (production). In dev without a DB,
// the web app detects the empty string and falls back to MockDataService.
// Per-company data-source connections come from RPT_company_connections
// in ConfigDb — not from appsettings — so there's no second connection
// to wire up at the AppHost layer.
var configConn = builder.Configuration.GetConnectionString("ConfigDb");

var dashboard = builder.AddProject<Projects.TleReportingDashboard_Web>("dashboard")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

var worker = builder.AddProject<Projects.TleReportingDashboard_Worker>("worker")
    .WithHttpHealthCheck("/health");

if (!string.IsNullOrEmpty(configConn))
{
    var configDb = builder.AddConnectionString("ConfigDb");
    dashboard.WithReference(configDb);
    worker.WithReference(configDb);
}

builder.Build().Run();
