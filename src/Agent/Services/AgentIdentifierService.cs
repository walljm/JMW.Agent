using Microsoft.Extensions.Options;

namespace JMW.Agent.Client.Services;

public interface IAgentIdentifierService
{
    Task<Guid> GetOrCreateAgentIdAsync();
    Task<string> GetOperatingSystemInfoAsync();
}

public sealed class AgentIdentifierService : IAgentIdentifierService
{
    private readonly AgentOptions options;
    private readonly ILogger<AgentIdentifierService> logger;

    public AgentIdentifierService(IOptions<AgentOptions> options, ILogger<AgentIdentifierService> logger)
    {
        this.options = options.Value ?? throw new ArgumentNullException(nameof(options), "AgentOptions configuration is missing");
        this.logger = logger;
    }

    public async Task<Guid> GetOrCreateAgentIdAsync()
    {
        try
        {
            if (File.Exists(options.AgentIdFilePath))
            {
                var content = await File.ReadAllTextAsync(options.AgentIdFilePath);
                if (Guid.TryParse(content.Trim(), out var existingId))
                {
                    logger.LogInformation("Loaded existing agent ID: {AgentId}", existingId);
                    return existingId;
                }

                logger.LogWarning("Invalid agent ID in file, generating new one");
            }

            // Generate new secure identifier
            var newId = Guid.NewGuid();
            await File.WriteAllTextAsync(options.AgentIdFilePath, newId.ToString());

            // Set restrictive file permissions (owner read/write only)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                await SetUnixFilePermissions(options.AgentIdFilePath);
            }

            logger.LogInformation("Generated new agent ID: {AgentId}", newId);
            return newId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get or create agent ID");
            throw;
        }
    }

    public Task<string> GetOperatingSystemInfoAsync()
    {
        var osInfo = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
        if (OperatingSystem.IsWindows())
        {
            osInfo = $"Windows {Environment.OSVersion.Version}";
        }
        else if (OperatingSystem.IsLinux())
        {
            osInfo = $"Linux {Environment.OSVersion.Version}";
        }
        else if (OperatingSystem.IsMacOS())
        {
            osInfo = $"macOS {Environment.OSVersion.Version}";
        }

        return Task.FromResult(osInfo);
    }

    private static async Task SetUnixFilePermissions(string filePath)
    {
        try
        {
            // Set file permissions to 600 (owner read/write only)
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();
        }
        catch (Exception)
        {
            // Ignore permission setting errors - not critical
        }
    }
}
