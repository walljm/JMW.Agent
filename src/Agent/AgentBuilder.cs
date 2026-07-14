using JMW.Discovery.Agent.Collection;

namespace JMW.Discovery.Agent;

/// <summary>
/// Fluent builder for Agent. Provide a config (file path or object),
/// register collectors, and call Build().
/// Usage:
/// var agent = new AgentBuilder()
/// .WithConfig("agent.json")
/// .WithCollector(new MySshCollector())
/// .WithCollector(new MySnmpCollector())
/// .WithStateDirectory("/var/lib/agent")
/// .Build();
/// await agent.RunAsync(CancellationToken.None);
/// </summary>
public sealed class AgentBuilder
{
    private AgentConfig? _config;
    private string _stateDir = ".agent-state";
    private readonly List<IDeviceCollector> _collectors = [];
    private readonly List<ILocalCollector> _localCollectors = [];
    private readonly List<IServiceCollector> _serviceCollectors = [];
    private readonly List<INetworkScanner> _networkScanners = [];
    private ITargetSource? _targetSource;
    private IAgentServerClient? _serverClient;

    public AgentBuilder WithConfig(string path) => WithConfig(AgentConfig.LoadFrom(path));

    public AgentBuilder WithConfig(AgentConfig config)
    {
        _config = config;
        return this;
    }

    /// <summary>Adds a remote device collector (SSH, SNMP, etc.).</summary>
    public AgentBuilder WithCollector(IDeviceCollector collector)
    {
        _collectors.Add(collector);
        return this;
    }

    /// <summary>Adds a local host collector (hardware, OS, Docker, etc.).</summary>
    public AgentBuilder WithLocalCollector(ILocalCollector collector)
    {
        _localCollectors.Add(collector);
        return this;
    }

    /// <summary>Adds a remote service collector (Technitium DNS, AdGuard Home, etc.).</summary>
    public AgentBuilder WithServiceCollector(IServiceCollector collector)
    {
        _serviceCollectors.Add(collector);
        return this;
    }

    /// <summary>
    /// Adds a network scanner (mDNS, SSDP, ARP, SNMP broadcast, etc.).
    /// All registered scanners run via NetworkDiscoveryCollector each cycle.
    /// </summary>
    public AgentBuilder WithNetworkScanner(INetworkScanner scanner)
    {
        _networkScanners.Add(scanner);
        return this;
    }

    public AgentBuilder WithStateDirectory(string path)
    {
        _stateDir = path;
        return this;
    }


    /// <summary>
    /// Override how targets are discovered. Defaults to the static list
    /// in the config file. Replace with a dynamic source later.
    /// </summary>
    public AgentBuilder WithTargetSource(ITargetSource source)
    {
        _targetSource = source;
        return this;
    }

    /// <summary>
    /// Override the server client. Defaults to HttpAgentServerClient.
    /// Useful for tests.
    /// </summary>
    public AgentBuilder WithServerClient(IAgentServerClient client)
    {
        _serverClient = client;
        return this;
    }

    public Agent Build()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Call WithConfig before Build.");
        }

        if (_collectors.Count == 0
         && _localCollectors.Count == 0
         && _serviceCollectors.Count == 0
         && _networkScanners.Count == 0)
        {
            throw new InvalidOperationException(
                "Register at least one collector via WithCollector, WithLocalCollector, WithServiceCollector, or WithNetworkScanner."
            );
        }

        ITargetSource targets = _targetSource ?? new ConfigTargetSource(_config.Targets);
        IAgentServerClient server = _serverClient ?? new HttpAgentServerClient(_config.ServerUrl);

        return new Agent(
            _config,
            _collectors,
            _localCollectors,
            _networkScanners,
            _serviceCollectors,
            targets,
            server,
            _stateDir
        );
    }
}