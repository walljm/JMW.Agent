namespace JMW.Agent.Client;

using Microsoft.AspNetCore.Hosting;

internal class Program
{
    public static async Task Main(string[] args) =>
        await CreateHostBuilder(args).RunConsoleAsync();

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging((context, builder) =>
            {
                builder
                    .ClearProviders()
                    .AddConsole();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder
                    .UseStartup<Startup>()
                    .UseKestrel()
                    ;
            })

            .UseSystemd()
        ;
}
