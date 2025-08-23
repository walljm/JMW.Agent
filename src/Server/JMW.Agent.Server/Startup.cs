using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using JMW.Agent.Server.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server;

public sealed class Startup
{
    public Startup(IConfiguration configuration)
    {
        this.Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        #region Generic configurations

        /*
            ZipArchive doesn't fully support asynchronous operations.
            https://github.com/dotnet/runtime/issues/1560
            https://github.com/dotnet/runtime/issues/1541
        */
        services.Configure<KestrelServerOptions>(static options =>
            {
                options.AllowSynchronousIO = true;
            }
        );

        services.Configure<JsonOptions>(static options =>
            {
                SystemTextJsonSerializerSettingsProvider.Apply(options.SerializerOptions);
            }
        );

        services.AddHealthChecks();
        services.AddMetricsCollector();

        #endregion Generic configurations

        #region Identity

        var connectionString = this.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connectionString)
        );

        // Configure Identity for both cookie and JWT authentication
        services.AddIdentity<ApplicationUser, IdentityRole>(static options =>
                {
                    options.SignIn.RequireConfirmedAccount = false;
                    options.Password.RequireDigit = false;
                    options.Password.RequiredLength = 6;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Password.RequireUppercase = false;
                    options.Password.RequireLowercase = false;
                }
            )
           .AddEntityFrameworkStores<ApplicationDbContext>()
           .AddDefaultTokenProviders();

        // Configure authentication with bearer token scheme
        services.AddAuthentication(static options =>
                {
                    // Set bearer token as default since we only use API endpoints
                    options.DefaultAuthenticateScheme = IdentityConstants.BearerScheme;
                    options.DefaultChallengeScheme = IdentityConstants.BearerScheme;
                }
            )
           .AddBearerToken(IdentityConstants.BearerScheme);

        // Simple authorization - all authenticated users can access protected endpoints
        services.AddAuthorization();

        // Register the placeholder email sender for both interfaces
        services.AddTransient<IEmailSender, EmailSender>();
        services.AddTransient<IEmailSender<ApplicationUser>, EmailSender>();

        services.AddControllersWithViews();

        #endregion Identity

        services.AddDatabaseDeveloperPageExceptionFilter();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Configure the HTTP request pipeline.
        if (env.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }
        else
        {
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseHttpMethodOverride();
        app.UseForwardedHeaders(
            new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.All,
                ForwardLimit = 3,
            }
        );

        app.UseStaticFiles();
        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(static endpoints =>
            {
                // api endpoints (protected)
                endpoints.AddServerEndpoints();

                // Map Identity API endpoints without authorization (public endpoints for login/register)
                endpoints.MapIdentityApi<ApplicationUser>();
            }
        );
    }
}