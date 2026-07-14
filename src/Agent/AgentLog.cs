using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JMW.Discovery.Agent;

/// <summary>
/// Provides the application-wide ILoggerFactory for the agent process.
/// Initialized once at startup in Program.cs before any collectors are constructed.
/// </summary>
internal static class AgentLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static ILoggerFactory Factory
    {
        get => _factory;
        set => _factory = value;
    }

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
}