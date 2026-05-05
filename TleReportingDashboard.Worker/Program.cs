using Hangfire;
using Hangfire.SqlServer;
using Serilog;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Services;
using TleReportingDashboard.Web.Services.QueryPipeline;
using TleReportingDashboard.Worker.Jobs;

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
        .WriteTo.Console()
        // Rolling daily file log under the deploy folder. Lets admins
        // tail Worker activity on IIS where stdout is normally
        // swallowed. Path is relative to ContentRootPath so it resolves
        // to <publish-folder>\Logs\worker-YYYYMMDD.log on IIS and the
        // bin/Debug equivalent during dev. retainedFileCountLimit caps
        // disk use; admins rotate by date instead of by size.
        .WriteTo.File(
            path: Path.Combine(builder.Environment.ContentRootPath, "Logs", "worker-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

    builder.AddServiceDefaults();

    // Schema config binding
    builder.Services.Configure<SchemaConfig>(
        builder.Configuration.GetSection("SchemaConfig"));

    // Services from Web project. EmailService + ExportService are pure-
    // logic helpers; QueryPipeline talks to per-company DBs to actually
    // run the report queries. NotificationService stays here too so the
    // job can drop "schedule_ran" / "schedule_failed" notifications into
    // the same inbox the Web app's bell icon reads from.
    builder.Services.AddScoped<IExportService, ExportService>();
    builder.Services.AddScoped<IEmailService, EmailService>();

    // SqlEmitter (IQueryPipeline) depends on schema config + per-company
    // connection resolution + dialect factory. Mirrors the Web project's
    // registrations from Program.cs so the worker can stand up the same
    // pipeline. Without these, the DI validator throws on container
    // build because SqlEmitter's constructor demands them.
    // ConfigDbCache wraps IMemoryCache — register it explicitly here.
    // The Web app gets it transitively from MudServices/Blazor; the
    // Worker has no such transitive provider, so DI validation fails
    // when ConfigDbCache's constructor parameter can't be resolved.
    builder.Services.AddMemoryCache();
    // EditorModeState is a circuit-scoped flag the Web app flips on the
    // schema-builder pages to bypass cache. Several services consume it
    // (CompanyConnectionAdminService, ReportDbService, etc.). On the
    // Worker side it's never enabled — we just need an instance in the
    // graph so DI can construct those services.
    builder.Services.AddScoped<EditorModeState>();
    builder.Services.AddSingleton<ConfigDbCache>();
    builder.Services.AddSingleton<ICompanyRegistry, CompanyRegistry>();
    builder.Services.AddScoped<ICompanyConnectionAdminService, CompanyConnectionAdminService>();
    builder.Services.AddSingleton<ICompanyConnectionResolver, DbCompanyConnectionResolver>();
    builder.Services.AddSingleton<TleReportingDashboard.Web.Services.QueryPipeline.ISqlDialectFactory,
                                   TleReportingDashboard.Web.Services.QueryPipeline.SqlDialectFactory>();
    builder.Services.AddSingleton<ISchemaConfigStore, SchemaConfigStore>();
    builder.Services.AddScoped<IQueryPipeline, SqlEmitter>();

    // The job class itself — Hangfire activates it from the scoped DI
    // container on every fire, picking up a fresh DbConnection / scoped
    // dependencies without leaking state across runs.
    builder.Services.AddScoped<ScheduledReportJob>();

    // Hangfire
    var configConnStr = builder.Configuration.GetConnectionString("ConfigDb");
    if (string.IsNullOrEmpty(configConnStr) && !builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Worker requires ConfigDb connection string. Set ConnectionStrings:ConfigDb in configuration.");
    }

    if (!string.IsNullOrEmpty(configConnStr))
    {
        // Notification service points at the same ConfigDb so Worker-
        // produced notifications land in the same RPT_user_notifications
        // table the Web app reads. Singleton because the service is
        // stateless beyond the connection string.
        builder.Services.AddSingleton<INotificationService, NotificationService>();

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

        // Polling reconciler — reads RPT_report_schedules every minute
        // and tells Hangfire which recurring jobs should currently be
        // registered. Without this, schedules saved via the Web UI
        // would sit in the table but never actually fire.
        builder.Services.AddHostedService<SchedulerSyncService>();
    }

    var app = builder.Build();
    app.MapDefaultEndpoints();

    if (!string.IsNullOrEmpty(configConnStr))
    {
        // Dashboard is gated by HangfireDashboardAuthFilter — open in
        // Development, restricted to Admins:Emails everywhere else.
        // Without the filter, /hangfire is reachable by anyone who can
        // hit the worker URL.
        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() }
        });
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
