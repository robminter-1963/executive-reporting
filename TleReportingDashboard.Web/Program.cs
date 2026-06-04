using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;
using Serilog;
using TleReportingDashboard.Web.Components;
using TleReportingDashboard.Web.Configuration;
using TleReportingDashboard.Web.Services;
using TleReportingDashboard.Web.Services.QueryPipeline;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // QuestPDF licensing — Community is free for any use as long as the
    // company's annual revenue is under $1M (or for OSS / personal /
    // non-profit use). Set once per process before any PDF is generated.
    // Re-evaluate when the revenue threshold changes — the package will
    // throw at PDF-render time if no license type is set.
    QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    var builder = WebApplication.CreateBuilder(args);

    // Serilog. The relative path "Logs/log-.txt" resolves against the
    // process working directory, which under IIS isn't the publish folder
    // — so the file logs vanished after deploy. Anchor on
    // AppContext.BaseDirectory (the publish folder) so the Logs folder
    // lands next to the DLLs regardless of how the host launches the app.
    var logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
    Directory.CreateDirectory(logDir);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(logDir, "log-.txt"), rollingInterval: RollingInterval.Day));

    // Aspire service defaults (OpenTelemetry, health checks, service discovery)
    builder.AddServiceDefaults();

    // MudBlazor — snackbars anchored to the bottom-right so "Preferences saved"
    // and similar ephemeral hints don't cover the main content near the top.
    // Durations tightened from MudBlazor defaults (1000 / 5000 / 2000 ms) to
    // snap shorter: confirmations are ephemeral and linger feels sluggish.
    // Errors go through the same pipeline — 2s is still long enough to read;
    // persistent-error conditions surface elsewhere (logs + inline alerts).
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
        config.SnackbarConfiguration.ShowTransitionDuration = 200;
        config.SnackbarConfiguration.VisibleStateDuration = 2000;
        config.SnackbarConfiguration.HideTransitionDuration = 250;
    });

    // Memory cache
    builder.Services.AddMemoryCache();

    // Per-circuit editor-mode flag + the ConfigDB cache wrapper. Both are
    // scoped so a single user's editor session bypasses cache without
    // affecting other users (cache content is shared via the singleton
    // IMemoryCache underneath). Editor pages flip the state with
    // `using var _ = EditorMode.Enter();` in OnInitializedAsync.
    builder.Services.AddScoped<EditorModeState>();
    builder.Services.AddSingleton<ConfigDbCache>();

    // Startup guard: Production/Staging must have Entra ID configured
    if (!builder.Environment.IsDevelopment())
    {
        var azureAdClientIdCheck = builder.Configuration["AzureAd:ClientId"];
        if (string.IsNullOrEmpty(azureAdClientIdCheck))
        {
            Log.Fatal("STARTUP BLOCKED: Entra ID (AzureAd:ClientId) is not configured. " +
                      "Production and Staging environments require Entra ID authentication. " +
                      "Set AzureAd configuration in appsettings or environment variables.");
            throw new InvalidOperationException(
                "Entra ID authentication is required in non-Development environments. " +
                "Configure AzureAd:ClientId, AzureAd:TenantId, and AzureAd:Domain.");
        }
    }

    // Schema config validation moved to after app build so it runs against the
    // resolved ISchemaConfigStore (DB-backed in prod, file-only in dev).

    // Export service (always available)
    builder.Services.AddScoped<IExportService, ExportService>();
    builder.Services.AddScoped<UserPreferenceState>();
    builder.Services.AddScoped<CurrentCompanyState>();
    builder.Services.AddScoped<LibraryNavState>();
    builder.Services.AddScoped<LandingGreetingState>();

    // HttpContext access for the ICurrentUserAccessor — needed so services
    // can resolve the signed-in user's email/oid without threading it
    // through every method signature. Required by AuditLogger.
    builder.Services.AddHttpContextAccessor();
    // Singleton: HttpContextCurrentUserAccessor depends only on
    // IHttpContextAccessor (which is Singleton-safe by design — uses
    // AsyncLocal internally). Registering as Singleton lets the audit
    // logger consume it from inside other Singleton services (AdminService,
    // CompanyRegistry, etc.) without a captive-dependency violation.
    builder.Services.AddSingleton<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

    // Admin allowlist (email-based; swap for claim-based later)
    builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admins"));
    builder.Services.AddSingleton<IAdminService, AdminService>();
    builder.Services.AddSingleton<ICompanyRegistry, CompanyRegistry>();
    builder.Services.AddScoped<ICompanyAdminService, CompanyAdminService>();
    builder.Services.AddScoped<ICompanyConnectionAdminService, CompanyConnectionAdminService>();
    // IHttpClientFactory backs the Dataverse "Test connection" path
    // (Entra token + WhoAmI ping). Registered once globally so the
    // factory's connection pooling / handler lifecycle works correctly
    // — using `new HttpClient()` per call would risk socket exhaustion
    // under load. Default-named client is fine; we set Timeout per use.
    builder.Services.AddHttpClient();
    builder.Services.AddScoped<IRoleService, RoleService>();
    builder.Services.AddScoped<IUserManagementService, UserManagementService>();
    builder.Services.AddScoped<IAdminAccessService, AdminAccessService>();
    builder.Services.AddScoped<TleReportingDashboard.Web.Services.Promotion.IPromotionPackageService,
                                TleReportingDashboard.Web.Services.Promotion.PromotionPackageService>();
    builder.Services.AddScoped<IQueryScopeResolver, QueryScopeResolver>();
    builder.Services.AddScoped<ITeamSourceService, TeamSourceService>();

    // Email service
    builder.Services.AddScoped<IEmailService, EmailService>();

    // Startup connection-string diagnostics (logs the resolved ConfigDb
    // server + database — credentials omitted). If the app is running stale
    // config, restart the process; hot-reload doesn't always pick it up.
    var configConnStr = builder.Configuration.GetConnectionString("ConfigDb");
    static string SummarizeConn(string? cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return "(empty)";
        try
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
            return $"{b.DataSource} / {b.InitialCatalog}";
        }
        catch (Exception ex) { return $"(invalid: {ex.Message})"; }
    }
    Log.Information("ConnectionStrings resolved: ConfigDb={ConfigSummary}",
        SummarizeConn(configConnStr));

    // Schema service always reads from schema_config.json via IOptionsSnapshot
    builder.Services.AddScoped<ISchemaService, SchemaService>();
    // Singleton — token cache lives across requests so a burst of metadata
    // calls during schema setup doesn't hit the Entra token endpoint
    // every time. Stateless apart from the cache; safe to share.
    builder.Services.AddSingleton<DataverseSchemaClient>();
    builder.Services.AddScoped<SchemaBuilderService>();
    builder.Services.AddScoped<ILibrarySectionService, LibrarySectionService>();
    builder.Services.AddScoped<ICompanyKpiService, CompanyKpiService>();
    builder.Services.AddScoped<IAppSettingsService, AppSettingsService>();
    // Scoped to match IAppSettingsService's lifetime — same circuit, same
    // instance, so ReportGrid + DetailViewer + AdminColumnWidthsTab share
    // one deserialized copy. Cross-circuit caching happens in the
    // underlying ConfigDbCache (singleton) so the JSON read isn't repeated
    // per circuit either.
    builder.Services.AddScoped<IColumnWidthDefaultsService, ColumnWidthDefaultsService>();

    // Per-company data-source connection resolver — reads RPT_company_connections
    // to materialize the ADO.NET connection string for the requested company.
    // Singleton because it caches resolved strings for the process lifetime.
    builder.Services.AddSingleton<ICompanyConnectionResolver, DbCompanyConnectionResolver>();

    // SQL dialect factory — maps connection_type ('sqlserver' | 'postgres')
    // to an ISqlDialect instance. Query emission and DbConnection creation
    // both route through the resolved dialect so the pipeline is provider-
    // agnostic for the SQL-family DBs we support.
    builder.Services.AddSingleton<TleReportingDashboard.Web.Services.QueryPipeline.ISqlDialectFactory,
                                   TleReportingDashboard.Web.Services.QueryPipeline.SqlDialectFactory>();

    // Mode selection:
    //   - ConfigDb set   → live mode (queries resolve their connection via
    //                      RPT_company_connections, RPT_* services persist
    //                      to the configured ConfigDb)
    //   - ConfigDb empty → mock mode (in-memory, for dev without a DB)
    if (!string.IsNullOrEmpty(configConnStr))
    {
        Log.Information("ConfigDb connection string found — live query mode enabled");
        // SOC-2 audit logger. Singleton — all dependencies (IConfiguration,
        // ICurrentUserAccessor via IHttpContextAccessor, ILogger) are
        // Singleton-safe, and SQL connections are short-lived per write
        // anyway. Singleton lifetime is required so Singleton services
        // (AdminService, CompanyRegistry-adjacent paths) can consume it
        // without DI captive-dependency violations.
        builder.Services.AddSingleton<IAuditLogger, AuditLogger>();
        builder.Services.AddScoped<IQueryPipeline, SqlEmitter>();
        builder.Services.AddScoped<IQueryService, QueryService>();
        builder.Services.AddScoped<ICodeSetService, CodeSetService>();
        // Filter-pickable Lookup values — fetched from the connection's
        // data DB, cached per (connectionId, lookupId). Scoped because
        // it consumes the scoped CompanyConnectionAdminService.
        builder.Services.AddScoped<ILookupValueService, LookupValueService>();
        builder.Services.AddScoped<INotificationService, NotificationService>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();

        builder.Services.AddScoped<ReportDbService>();
        builder.Services.AddScoped<IReportService>(sp => sp.GetRequiredService<ReportDbService>());
        builder.Services.AddScoped<ISharingService>(sp => sp.GetRequiredService<ReportDbService>());
        builder.Services.AddScoped<IScheduleService>(sp => sp.GetRequiredService<ReportDbService>());
        builder.Services.AddScoped<IUserPreferenceService, UserPreferenceService>();
        builder.Services.AddScoped<IMasterDashboardService, MasterDashboardService>();
        builder.Services.AddScoped<IGridTemplateService, GridTemplateService>();
        builder.Services.AddScoped<IFieldReferenceService, FieldReferenceService>();
        builder.Services.AddSingleton<ISchemaConfigStore, SchemaConfigStore>();
        builder.Services.AddScoped<ICustomPrimaryTableService, CustomPrimaryTableService>();
        builder.Services.AddScoped<ISchemaConfigHistoryService, SchemaConfigHistoryService>();
        builder.Services.AddScoped<IFavoriteService, FavoriteService>();
    }
    else
    {
        Log.Information("No ConfigDb connection string — using in-memory mocks for dev");
        // Dev-mode audit logger keeps a bounded in-process ring (a static
        // field inside the impl) so the Admin → Audit Log tab renders
        // entries from any circuit. Singleton to match the SQL-mode
        // lifetime — keeps the captive-dependency story consistent
        // whether the app runs in mock or live mode.
        builder.Services.AddSingleton<IAuditLogger, InMemoryAuditLogger>();
        builder.Services.AddSingleton<MockDataService>();
        builder.Services.AddSingleton<IQueryService>(sp => sp.GetRequiredService<MockDataService>());
        builder.Services.AddSingleton<ICodeSetService, MockCodeSetService>();
        // Dev mode has no per-connection data DB to query — register a
        // no-op so the lookup-backed filter UI just renders empty
        // pickers instead of crashing on a null connection resolver.
        builder.Services.AddSingleton<ILookupValueService, NoopLookupValueService>();
        builder.Services.AddSingleton<IReportService>(sp => sp.GetRequiredService<MockDataService>());
        builder.Services.AddSingleton<ISharingService>(sp => sp.GetRequiredService<MockDataService>());
        builder.Services.AddSingleton<IScheduleService>(sp => sp.GetRequiredService<MockDataService>());
        builder.Services.AddScoped<IUserPreferenceService, InMemoryUserPreferenceService>();
        builder.Services.AddSingleton<INotificationService, InMemoryNotificationService>();
        builder.Services.AddSingleton<IThemeService, InMemoryThemeService>();
        builder.Services.AddScoped<IMasterDashboardService, MasterDashboardService>();
        builder.Services.AddScoped<IGridTemplateService, GridTemplateService>();
        builder.Services.AddSingleton<IFieldReferenceService, NoopFieldReferenceService>();
        builder.Services.AddSingleton<ISchemaConfigStore, InMemorySchemaConfigStore>();
        builder.Services.AddSingleton<ICustomPrimaryTableService, InMemoryCustomPrimaryTableService>();
        // Mock mode has no DB to persist history into, so the service
        // returns an empty list and accepts deletes as no-ops. Keeps the
        // Admin → Schema History tab from crashing in dev-without-DB mode.
        builder.Services.AddSingleton<ISchemaConfigHistoryService, NoopSchemaConfigHistoryService>();
        builder.Services.AddSingleton<IFavoriteService, InMemoryFavoriteService>();
    }

    // Auth auto-detect: Entra ID or stub
    var azureAdClientId = builder.Configuration["AzureAd:ClientId"];
    var entraIdEnabled = !string.IsNullOrEmpty(azureAdClientId);
    if (entraIdEnabled)
    {
        // Production: Entra ID SSO
        Log.Information("AzureAd configuration found — using Entra ID authentication");
        builder.Services.AddAuthentication(Microsoft.Identity.Web.Constants.AzureAd)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

        // Microsoft.Identity.Web.UI ships the controller endpoints
        // /MicrosoftIdentity/Account/SignIn + /SignOut + the OIDC
        // callback. Without these registered there's no logout target
        // and no interactive challenge surface — challenges would
        // 404 or stall.
        builder.Services
            .AddControllersWithViews()
            .AddMicrosoftIdentityUI();

        // Global authentication gate: every request must be authenticated.
        // Combined with RequireAuthorization() on MapRazorComponents below,
        // this turns first-hit traffic on a logged-out browser into an
        // OIDC challenge → Entra sign-in flow. Anonymous visitors never
        // reach a Razor component.
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = options.DefaultPolicy;
        });
    }
    else
    {
        // Dev mode: stub authentication
        Log.Information("No AzureAd configuration — using stub authentication (Dev User)");
        builder.Services.AddAuthentication("Stub")
            .AddScheme<AuthenticationSchemeOptions, StubAuthenticationHandler>("Stub", null);
        builder.Services.AddAuthorization();
    }

    // Forwarded headers: when the app runs behind IIS (or any reverse
    // proxy), the inbound request's true scheme/host arrives in
    // X-Forwarded-* headers — Kestrel sees the proxy address. Without
    // this, the OIDC redirect URI would be built from the internal
    // host and Entra would reject the callback. KnownNetworks/Proxies
    // are deliberately empty: in IIS in-process hosting the loopback
    // forwarder is already trusted; if you front Kestrel with a
    // separate proxy, list its IP/CIDR here.
    builder.Services.Configure<ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders =
              ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        opts.KnownIPNetworks.Clear();
        opts.KnownProxies.Clear();
    });

    builder.Services.AddCascadingAuthenticationState();

    // Blazor
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // Validate the resolved schema config (DB or file, whichever the store picked).
    // Fatal-fails startup so bad config can never go live — same guarantee as before.
    try
    {
        var schemaConfig = app.Services.GetRequiredService<ISchemaConfigStore>().Current;
        if (schemaConfig is not null && schemaConfig.Fields.Count > 0)
        {
            var warnings = SchemaConfigValidator.Validate(schemaConfig);
            Log.Information("Schema config validated: {FieldCount} fields, {JoinCount} joins, {WarningCount} warnings",
                schemaConfig.Fields.Count, schemaConfig.Joins.Count, warnings.Count);
            // Non-fatal field misconfigurations (e.g. empty SourceColumn)
            // surface here so an admin can find and fix them without the
            // app refusing to start. These fields are skipped at query
            // time, not executed with broken SQL.
            foreach (var warning in warnings)
                Log.Warning("Schema config warning: {Warning}", warning);
        }
    }
    catch (InvalidOperationException ex)
    {
        Log.Fatal(ex, "Schema config validation failed — application refused to start");
        throw;
    }

    // Forwarded headers must run BEFORE UsePathBase / UseAuthentication so
    // every downstream component sees the externally-visible scheme/host.
    // Skipped in Development (Kestrel directly serves the browser, no
    // proxy hops, no headers to honor).
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    // When hosted as an IIS sub-application (e.g. localhost/ReportingDashboard),
    // UsePathBase strips the prefix so routing and static files resolve correctly.
    // Only activate when the PATH_BASE env var / config key is set — in local dev
    // (Aspire/Kestrel at root) it's absent and this is a no-op.
    var pathBase = builder.Configuration["PATH_BASE"];
    if (!string.IsNullOrEmpty(pathBase))
        app.UsePathBase(pathBase);

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    }
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.UseStaticFiles();
    app.MapStaticAssets();

    // Microsoft.Identity.Web.UI controllers (SignIn / SignOut / OIDC
    // callback) are registered only when Entra is configured. In stub-
    // auth mode there's nothing for them to call into and routing would
    // shadow real endpoints with 404s, so we skip the mapping entirely.
    if (entraIdEnabled)
    {
        app.MapControllers();
    }

    var razorEndpoints = app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();
    if (entraIdEnabled)
    {
        // FallbackPolicy already gates everything; the explicit
        // RequireAuthorization on the components endpoint nails the
        // belt to the suspenders so an unauthenticated request to a
        // Blazor route triggers a challenge instead of falling through
        // to a 401 page.
        razorEndpoints.RequireAuthorization();
    }

    // Dev-only user impersonation endpoints. Dropped in Production — the
    // stub auth scheme doesn't activate there (refuses to authenticate), so
    // these routes would be dead weight at best and a foot-gun at worst.
    if (app.Environment.IsDevelopment())
    {
        // Set the impersonation cookie. The cookie is session-scoped (no
        // Expires) so it clears when the browser tab closes, and HttpOnly
        // so JS can't read it. Redirects home after setting.
        app.MapGet("/dev/switch-user", (string? email, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                ctx.Response.Cookies.Delete(StubAuthenticationHandler.ImpersonationCookieName);
            }
            else
            {
                ctx.Response.Cookies.Append(
                    StubAuthenticationHandler.ImpersonationCookieName,
                    email.Trim(),
                    new CookieOptions { HttpOnly = true, Path = "/", SameSite = SameSiteMode.Lax });
            }
            return Results.Redirect("/?all=1");
        });

        app.MapGet("/dev/clear-user", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(StubAuthenticationHandler.ImpersonationCookieName);
            return Results.Redirect("/?all=1");
        });
    }

    // Aspire health check + alive endpoints
    app.MapDefaultEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
