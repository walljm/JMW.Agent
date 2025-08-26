using JMW.Agent.Server;

internal class Program
{
    public static async Task Main(string[] args) =>
        await CreateHostBuilder(args).RunConsoleAsync();

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(static (_, builder) =>
            {
                builder
                    .ClearProviders()
                    .AddConsole();
            })
            .ConfigureWebHostDefaults(static webBuilder =>
            {
                webBuilder
                   .UseStartup<Startup>()
                   .UseKestrel()
                    ;
            })
            ;
}