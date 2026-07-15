using JMW.Discovery.Agent;
using JMW.Discovery.Agent.Collection.Device;
using JMW.Discovery.Agent.Collection.Local;
using JMW.Discovery.Agent.Collection.Network;

using Microsoft.Extensions.Logging;

CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AgentLog.Factory = LoggerFactory.Create(b =>
    {
        b.AddConsole();
        b.SetMinimumLevel(LogLevel.Information);
    }
);

string configPath = args.Length > 0 ? args[0] : "agent.json";
string stateDir = Environment.GetEnvironmentVariable("JMW_AGENT_STATE_DIR")
 ?? "/var/lib/jmw-agent";

// Warn loudly at startup if the update public key was not baked in. The agent
// will run normally, but self-update will be permanently unavailable since every
// update attempt will be rejected at signature-verification time.
if (string.IsNullOrWhiteSpace(JMW.Discovery.Agent.Collection.UpdatePublicKey.Value))
{
    Console.Error.WriteLine(
        "[WARN] UpdatePublicKey.Value is empty. This binary cannot receive self-updates. "
      + "Rebuild with a real signing key before deploying to production."
    );
}

Agent agent = new AgentBuilder()
    .WithConfig(configPath)
    .WithStateDirectory(stateDir)
    // Local collectors — each covers a distinct aspect of the host.
    // The agent runs all supported collectors each cycle and sends the
    // combined delta to the server.
    .WithLocalCollector(new HardwareCollector())
    .WithLocalCollector(new OsCollector())
    .WithLocalCollector(new NetworkCollector())
    .WithLocalCollector(new DiskCollector())
    .WithLocalCollector(new FilesystemCollector())
    .WithLocalCollector(new ProcessCollector())
    .WithLocalCollector(new PortCollector())
    .WithLocalCollector(new ServiceCollector())
    .WithLocalCollector(new DockerCollector())
    .WithLocalCollector(new SecurityCollector())
    .WithLocalCollector(new BatteryCollector())
    .WithLocalCollector(new HwInventoryCollector())
    .WithLocalCollector(new UserCollector())
    .WithLocalCollector(new UpdatesCollector())
    .WithLocalCollector(new RouteCollector())
    .WithLocalCollector(new ArpCollector())
    .WithLocalCollector(new CertScanCollector())
    .WithLocalCollector(new StepClientCollector())
    // Auto-discovers step-ca if the step CLI and ca.json are present on this host.
    // No configuration required — the root cert fingerprint is the service identity.
    .WithLocalCollector(new StepCaCollector())
    .WithLocalCollector(new RebootHistoryCollector())
    .WithLocalCollector(new PackageCollector())
    .WithLocalCollector(new GpuCollector())
    .WithLocalCollector(new DhcpLeaseCollector())
    // Service collectors handle targets listed in agent.json "targets": [...].
    // Register the collector type here; endpoints and credentials live in the config file.
    .WithServiceCollector(new TechnitiumCollector())
    .WithServiceCollector(new HomeAssistantCollector())
    // Network discovery scanners run against all local subnets each cycle.
    // They discover unknown devices via broadcast/multicast and active probing.
    .WithNetworkScanner(new ArpScanner())
    .WithNetworkScanner(new MdnsScanner())
    .WithNetworkScanner(new SsdpScanner())
    .WithNetworkScanner(new SnmpBroadcastScanner())
    .WithNetworkScanner(new GatewaySnmpArpScanner())
    .WithNetworkScanner(new NbnsScanner())
    .WithNetworkScanner(new LlmnrScanner())
    .WithNetworkScanner(new WsDiscoveryScanner())
    .WithNetworkScanner(new DnsPtrScanner())
    .WithNetworkScanner(new HttpBannerScanner())
    .WithNetworkScanner(new TlsCertScanner())
    .WithNetworkScanner(new Smb2Scanner())
    .WithNetworkScanner(new SshBannerScanner())
    .WithNetworkScanner(new LdapScanner())
    .WithNetworkScanner(new EurekaScanner())
    .WithNetworkScanner(new IppScanner())
    .WithNetworkScanner(new SnmpPrinterScanner())
    .WithNetworkScanner(new RokuScanner())
    .WithNetworkScanner(new AirPlayScanner())
    // IoT protocol probes — device-class-specific active fingerprinting.
    .WithNetworkScanner(new PingSweepScanner())
    .WithNetworkScanner(new CoApScanner())
    .WithNetworkScanner(new RtspScanner())
    .WithNetworkScanner(new MqttScanner())
    .WithNetworkScanner(new PhilipsHueScanner())
    .WithNetworkScanner(new OnvifScanner())
    // Building automation and industrial IoT protocols — read-only, no external libraries.
    .WithNetworkScanner(new BacnetScanner())
    .WithNetworkScanner(new ModbusScanner())
    // Remote device collectors — optional, omit if this agent only monitors the local host.
    .WithCollector(new SshCollector())
    .WithCollector(new SnmpCollector())
    .WithCollector(new BacnetCollector())
    .WithCollector(new ModbusCollector())
    // Cloud-API device collector — reaches a Google Wifi / Nest Wifi mesh through
    // the Google cloud API and emits its connected clients as discovered devices.
    .WithCollector(new GoogleWifiCollector())
    .Build();

await agent.RunAsync(cts.Token);