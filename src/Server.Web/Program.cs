using System.Text.Json;
using System.Threading.RateLimiting;

using ITPIE.Migrations;

using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Agents;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Infrastructure;
using JMW.Discovery.Server.Projections;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Configure minimal API JSON binding to use snake_case so agents can send
// standard snake_case JSON (e.g. agent_id, not AgentId).
builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    }
);

string connectionString = Environment.GetEnvironmentVariable("JMW_DB_CONNECTION")
 ?? throw new InvalidOperationException("JMW_DB_CONNECTION environment variable is required.");

NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
builder.Services.AddSingleton(dataSource);

// Register database migrations (runs as a BackgroundService after app.Run).
// All other startup work that requires the schema must await MigrationCompletedSignal.Completed.
builder.Services.AddDatabaseMigrations();

string keyRingPath = Environment.GetEnvironmentVariable("JMW_KEY_RING_PATH")
 ?? throw new InvalidOperationException("JMW_KEY_RING_PATH environment variable is required.");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("JMW.Discovery.Server");

builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-CSRF-TOKEN";
    }
);

builder.Services.AddRazorPages();

// RFC 7807 problem+json is the API error contract; see Api/ApiError.cs.
builder.Services.AddProblemDetails();

builder.Services.AddScoped<AuditLog>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<BootstrapSetupToken>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<TrustedCaProvider>();
builder.Services.AddScoped<AgentConfigAssembler>();

// ReleaseManager indexes published agent binaries for the self-update mechanism;
// JMW_RELEASES_DIR unset/empty disables it (Enabled == false), mirroring the
// JMW_TRUSTED_CA_PATH convention above.
builder.Services.AddSingleton(new ReleaseManager(Environment.GetEnvironmentVariable("JMW_RELEASES_DIR")));
builder.Services.AddHostedService<ReleaseRescanService>();

// Ingest pipeline services
builder.Services.AddSingleton<MetricsRepository>();
builder.Services.AddSingleton<FactRepository>();
builder.Services.AddSingleton(sp =>
    {
        NpgsqlDataSource ds = sp.GetRequiredService<NpgsqlDataSource>();
        return new ProjectionRouter(ds, ProjectionLibrary.CreateAll(ds));
    }
);
// Fact analysis (normalize + derive) — agents emit raw; the server normalizes at ingest.
builder.Services.AddSingleton(AnalysisLibrary.CreateEngine());
builder.Services.AddSingleton(sp =>
    new IncidentEvaluator(sp.GetRequiredService<NpgsqlDataSource>(), IncidentTypeRegistry.CreateAll())
);
builder.Services.AddSingleton<FactIngestPipeline>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<ServiceRegistry>();
builder.Services.AddScoped<DiscoveryMaterializer>();

// Agent-offline incidents are silence-driven (absence of a heartbeat), not fact-value-driven, so
// they need a periodic sweep rather than an IncidentEvaluator hook — see AgentLivenessSweepService.
builder.Services.AddHostedService<AgentLivenessSweepService>();

// Fingerprint conflicts are an emergent property of device_fingerprints, not something a single
// ingest call site "creates" — see FingerprintConflictSweepService.
builder.Services.AddHostedService<FingerprintConflictSweepService>();

// BootstrapService now runs as a hosted service so it correctly awaits migrations.
builder.Services.AddHostedService<BootstrapService>();

// Ensures projection tables/columns declared in ProjectionLibrary exist (additive DDL,
// generated from the library) after migrations — so a new projection needs no migration.
builder.Services.AddHostedService<ProjectionSchemaService>();

// RetentionService registered as a singleton so the admin endpoint can call TriggerAsync directly.
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

// Partition maintenance for metrics_raw — provisions future day partitions, drops expired
// ones. Sibling to RetentionService but for partitioned tables (see docs/plans/metrics-retention.md).
builder.Services.AddHostedService<MetricPartitionService>();

// OuiUpdateService registered as a singleton so both the admin endpoint and the
// heartbeat endpoint can access CurrentVersionHash and TriggerAsync.
builder.Services.AddHttpClient("oui")
    .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(120);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("JMW-Discovery/1.0 (OUI updater)");
        }
    );
builder.Services.AddSingleton<OuiUpdateService>();
builder.Services.AddHostedService<OuiInitService>();

// OIDC SSO — fully configured at deploy time via env vars, or fully absent. No partial/
// DB-editable state. See OidcBridge for how a successful OIDC login becomes a jmw_session.
OidcOptions oidcOptions = new(
    Environment.GetEnvironmentVariable("JMW_OIDC_AUTHORITY"),
    Environment.GetEnvironmentVariable("JMW_OIDC_CLIENT_ID"),
    Environment.GetEnvironmentVariable("JMW_OIDC_CLIENT_SECRET"),
    Environment.GetEnvironmentVariable("JMW_OIDC_CALLBACK_PATH")
);
builder.Services.AddSingleton(oidcOptions);

// Register a cookie challenge scheme so that unauthorized requests redirect to /Login
// instead of throwing. Our SessionMiddleware handles the actual session loading;
// this only provides the DefaultChallengeScheme for the authorization pipeline. The OIDC
// handler below (when enabled) also targets this same scheme as its SignInScheme — purely a
// structural requirement of AddOpenIdConnect; OidcBridge intercepts before that sign-in
// actually happens, so this cookie is never persisted by either path.
AuthenticationBuilder authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
        {
            options.LoginPath = "/Login";
            options.AccessDeniedPath = "/Login";
        }
    );

if (oidcOptions.Enabled)
{
    authBuilder.AddOpenIdConnect("oidc", options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Authority = oidcOptions.Authority;
            options.ClientId = oidcOptions.ClientId;
            options.ClientSecret = oidcOptions.ClientSecret;
            options.CallbackPath = oidcOptions.CallbackPath;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            // OnTokenValidated always fires for every successful login (unlike
            // OnUserInformationReceived, which depends on the provider supporting a UserInfo
            // endpoint) — rely on the email scope being included directly in the ID token
            // rather than an extra UserInfo round-trip whose completion isn't guaranteed.
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("email");
            options.Scope.Add("profile");
            options.Events.OnTokenValidated = OidcBridge.HandleTokenValidatedAsync;
        }
    );
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RbacPolicies.Admin, RbacPolicies.AdminPolicy)
    .AddPolicy(RbacPolicies.Authenticated, RbacPolicies.AuthenticatedPolicy);

// IP-based fixed-window rate limiter for the agent registration endpoint.
// Limits registration attempts to 10 per minute per source IP.
builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
    }
);

builder.Services.AddMemoryCache();

builder.Services.AddOutputCache(o =>
    {
        o.AddBasePolicy(b => b.NoCache());
    }
);

builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy(
            "agent-register",
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
                )
        );
        // PERF-046: rate-limit the ingest endpoint — 120 requests/min per source IP
        // (2/sec sustained; well above any real agent cycle frequency).
        options.AddPolicy(
            "agent-facts",
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
                )
        );
        options.AddPolicy(
            "bootstrap",
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
                )
        );
        options.RejectionStatusCode = 429;
    }
);

WebApplication app = builder.Build();

app.UseExceptionHandler(appError =>
    appError.Run(async context =>
        {
            await ErrorEnvelopeMiddleware.HandleAsync(context, app.Logger);
        }
    )
);

app.Use(async (context, next) =>
    {
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        context.Response.Headers.Append(
            "Content-Security-Policy",
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:"
        );
        await next();
    }
);

app.UseResponseCompression();
{
    // Default KnownIPNetworks/KnownProxies only trust loopback as the immediate proxy hop.
    // Docker's port-forwarding NATs the connection so it arrives looking like it came from
    // the bridge-network gateway, not 127.0.0.1 — under the default trust list, that made
    // X-Forwarded-Proto silently ignored for every request to this container (regardless of
    // reverse proxy), so the app always thought it was on http even behind real TLS
    // termination. This container is only ever reachable through infrastructure the
    // operator controls (a reverse proxy, Tailscale Serve, etc.), so trust any hop.
    // Note: `KnownIPNetworks = { }` in an object initializer is a no-op (it calls Add() zero
    // times, it doesn't clear the defaults) — Clear() must be called explicitly.
    // XForwardedHost is required too — without it, Request.Host (and anything built from it,
    // like the cookie-auth redirect-to-login Location header) reflects Kestrel's own bind
    // address/whatever raw Host the proxy hop used, not the real public hostname.
    ForwardedHeadersOptions forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedHost,
    };
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);
}
app.UseHttpsRedirection();
// PERF-044: serve static assets with a 1-hour cache to reduce repeat downloads.
app.UseStaticFiles(
    new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=3600";
        },
    }
);
app.UseRouting();
app.UseRateLimiter();

app.UseMiddleware<SessionMiddleware>();
app.UseMiddleware<AgentApiKeyMiddleware>();

// UseAuthentication runs the cookie scheme but finds no .AspNetCore.Cookies
// cookie (we use jmw_session) so it leaves context.User unchanged.
// It must be present for ChallengeAsync to redirect to /Login on 401/403.
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();
app.UseAntiforgery();

app.MapGet(
    "/healthz",
    () => Results.Ok(
        new
        {
            status = "ok",
        }
    )
);

app.MapGet(
    "/readyz",
    async (NpgsqlDataSource db, MigrationCompletedSignal migrationSignal) =>
    {
        // Report not-ready until migrations have completed.
        if (!migrationSignal.Completed.IsCompleted)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "migrations_pending",
                        message = "Database migrations are still running.",
                    },
                },
                statusCode: 503
            );
        }

        try
        {
            await using NpgsqlConnection conn = await db.OpenConnectionAsync();
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
        }
        catch (Exception ex)
        {
            ProgramLog.DbConnectFailed(app.Logger, ex);
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "db_unavailable",
                        message = "Database is unavailable.",
                    },
                },
                statusCode: 503
            );
        }

        string? keyRingDir = Environment.GetEnvironmentVariable("JMW_KEY_RING_PATH");
        if (!string.IsNullOrEmpty(keyRingDir) && !Directory.Exists(keyRingDir))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "key_ring_unavailable",
                        message = "Key ring path is not accessible.",
                    },
                },
                statusCode: 503
            );
        }

        return Results.Ok(
            new
            {
                status = "ok",
            }
        );
    }
);

// Auth endpoints sit outside versioning — /auth/* and /bootstrap don't carry a version.
AuthEndpoints.Map(app);
BootstrapEndpoints.Map(app);

// All API endpoints live under /api/v1. A future v2 is a new MapGroup call.
RouteGroupBuilder v1 = app.MapGroup("/api/v1");

// Agent-facing endpoints — authenticated by AgentApiKeyMiddleware.
RouteGroupBuilder agentGroup = v1.MapGroup("/agent");
AgentRegistrationEndpoint.Map(agentGroup);
HeartbeatEndpoint.Map(agentGroup);
FactsEndpoint.Map(agentGroup);
AgentReleaseDownloadEndpoint.Map(agentGroup);

// Admin endpoints — require admin role. The filter below makes the X-CSRF-TOKEN header the admin
// UI already sends actually get validated on state-changing requests (GET/HEAD/OPTIONS/TRACE are
// left alone, matching how AntiforgeryMiddleware itself treats safe methods).
RouteGroupBuilder adminGroup = v1.MapGroup("/admin")
    .RequireAuthorization(RbacPolicies.Admin)
    .AddEndpointFilter(async (context, next) =>
        {
            string method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
             || HttpMethods.IsTrace(method))
            {
                return await next(context);
            }

            IAntiforgery antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return ApiError.Problem(400, "invalid_csrf_token", "Missing or invalid CSRF token.");
            }

            return await next(context);
        }
    );
AgentsApi.Map(adminGroup);
CredentialsApi.Map(adminGroup);
AgentConfigApi.Map(adminGroup);
TargetsApi.Map(adminGroup);
RetentionApi.Map(adminGroup);
OuiApi.Map(adminGroup);
ConflictsApi.Map(adminGroup);
AgentLivenessSettingsApi.Map(adminGroup);
UsersApi.Map(adminGroup);
DeviceFactsApi.Map(adminGroup);
CustomFieldsApi.Map(adminGroup);

// Reporting endpoints — require any authenticated user.
RouteGroupBuilder reportGroup = v1.MapGroup("")
    .RequireAuthorization(ReadPolicy.Name);
DevicesApi.Map(reportGroup);
DeviceListApi.Map(reportGroup);
DashboardApi.Map(reportGroup);
ServicesApi.Map(reportGroup);
StorageApi.Map(reportGroup);
PortsApi.Map(reportGroup);
HardwareApi.Map(reportGroup);
InterfacesApi.Map(reportGroup);
ComponentsApi.Map(reportGroup);
ContainersApi.Map(reportGroup);
ArpApi.Map(reportGroup);
SubnetsApi.Map(reportGroup);
L2TopologyApi.Map(reportGroup);
ChangesApi.Map(reportGroup);
TerrainApi.Map(reportGroup);

// Agents/AgentDetail moved from /admin/agents to /fleet/agents — keep old bookmarks/links
// working for one release. Query string (filters, cursors) carries through unchanged.
app.MapGet(
    "/admin/agents",
    (HttpContext ctx) => Results.Redirect("/fleet/agents" + ctx.Request.QueryString, permanent: true)
);
app.MapGet(
    "/admin/agents/{id:guid}",
    (HttpContext ctx, Guid id) => Results.Redirect($"/fleet/agents/{id}" + ctx.Request.QueryString, permanent: true)
);

app.MapRazorPages();

await app.RunAsync();

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Database connectivity check failed.")]
    public static partial void DbConnectFailed(ILogger logger, Exception ex);
}