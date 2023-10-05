using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
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
        services.Configure<KestrelServerOptions>(options =>
        {
            options.AllowSynchronousIO = true;
        });

        services.Configure<JsonOptions>(options =>
        {
            SystemTextJsonSerializerSettingsProvider.Apply(options.SerializerOptions);
        });

        services.AddHealthChecks();
        services.AddMetricsCollector();

        #endregion Generic configurations

        #region Identity

        var connectionString = this.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(connectionString)
        );

        services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
            .AddEntityFrameworkStores<ApplicationDbContext>();

        services.AddIdentityServer()
            .AddApiAuthorization<ApplicationUser, ApplicationDbContext>();

        services.AddAuthentication()
            .AddIdentityServerJwt()
            ;

        services.AddControllersWithViews();
        services.AddRazorPages();

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
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.All,
            ForwardLimit = 3
        });

        app.UseStaticFiles();
        app.UseRouting();

        app.UseIdentityServer();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapRazorPages(); // for identity

            // api endpoints
            endpoints.AddServerEndpoints();
            endpoints.AddOidcEndpoints();
        });
    }
}