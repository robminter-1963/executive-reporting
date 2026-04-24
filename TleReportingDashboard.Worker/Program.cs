using Hangfire;
using Hangfire.SqlServer;
using Serilog;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Services;
using TleReportingDashboard.Web.Services.QueryPipeline;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.AddServiceDefaults();

    // Schema config binding
    builder.Services.Configure<SchemaConfig>(
        builder.Configuration.GetSection("SchemaConfig"));

    // Services from Web project
    builder.Services.AddScoped<IExportService, ExportService>();
    builder.Services.AddScoped<IEmailService, EmailService>();
    builder.Services.AddScoped<IQueryPipeline, SqlEmitter>();

    // Hangfire
    var configConnStr = builder.Configuration.GetConnectionString("ConfigDb");
    if (string.IsNullOrEmpty(configConnStr) && !builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Worker requires ConfigDb connection string. Set ConnectionStrings:ConfigDb in configuration.");
    }

    if (!string.IsNullOrEmpty(configConnStr))
    {
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(configConnStr, new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));
        builder.Services.AddHangfireServer();
    }

    var app = builder.Build();
    app.MapDefaultEndpoints();

    if (!string.IsNullOrEmpty(configConnStr))
    {
        app.MapHangfireDashboard("/hangfire");
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
