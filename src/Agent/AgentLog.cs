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

    /// <summary>
    /// Process-wide ring of recent formatted log lines, shared between the
    /// <see cref="RingBufferLoggerProvider"/> (writes) that Program.cs registers on the logging
    /// pipeline and the <see cref="AgentLogCollector"/> (reads) that serves on-demand log pulls.
    /// The fallback capture source on every non-systemd host (docs/plans/agent-log-viewer.md §4.1).
    /// </summary>
    public static LogRingBuffer Buffer { get; } = new();

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
}