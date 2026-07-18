using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects Docker daemon and container facts via the Docker Engine HTTP API
/// over the local Unix socket (Linux/macOS) or named pipe (Windows).
/// Avoids the Docker CLI and the Moby SDK: both are slow or heavyweight.
/// The Engine API is stable (v1.43+) and requires no extra dependencies.
/// </summary>
public sealed class DockerCollector : ILocalCollector
{
    private static readonly string[] SocketCandidates =
    [
        "/var/run/docker.sock",
        // rootless Docker / colima / Docker Desktop
        $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.docker/run/docker.sock",
        $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.colima/default/docker.sock",
    ];

    // Shared HttpClient instances keyed by socket path (or "win://pipe/docker_engine").
    // HttpClient and SocketsHttpHandler are thread-safe and designed to be reused across calls.
    private static readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly ILogger<DockerCollector> _logger = AgentLog.CreateLogger<DockerCollector>();

    public string Name => "docker";

    public bool IsSupported =>
        OperatingSystem.IsLinux()
     || OperatingSystem.IsMacOS()
     || (OperatingSystem.IsWindows() && File.Exists(@"\\.\pipe\docker_engine"));

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        HttpClient http;
        if (OperatingSystem.IsWindows())
        {
            http = GetOrCreateWindowsClient();
        }
        else
        {
            string? sock = SocketCandidates.FirstOrDefault(File.Exists);
            if (sock is null)
            {
                return []; // Docker not installed — not an error
            }

            http = _clients.GetOrAdd(sock, BuildUnixClient);
        }

        List<Fact> facts = new();

        try
        {
            await CollectDaemonInfoAsync(deviceId, http, facts, ct);
            await CollectContainersAsync(deviceId, http, facts, ct);
            await CollectNetworksAsync(deviceId, http, facts, ct);
        }
        catch (Exception ex)
        {
            // Docker present but inaccessible (permission, daemon stopped).
            // Return whatever we collected so far rather than throwing.
            DockerCollectorLog.CollectionError(_logger, ex);
        }

        return facts;
    }

    private static async Task CollectDaemonInfoAsync(
        string deviceId,
        HttpClient http,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        HttpResponseMessage resp = await http.GetAsync("/v1.43/info", ct);
        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct
        );

        JsonElement root = doc.RootElement;
        string[] keys = [deviceId];

        facts.Add(Fact.Create(FactPaths.DockerVersion, keys, root.GetStr("ServerVersion") ?? ""));
        facts.Add(Fact.Create(FactPaths.DockerApiVersion, keys, root.GetStr("ApiVersion") ?? ""));
        facts.Add(Fact.Create(FactPaths.DockerStorageDriver, keys, root.GetStr("Driver") ?? ""));
        facts.Add(Fact.Create(FactPaths.DockerContainersRunning, keys, root.GetInt("ContainersRunning")));
        facts.Add(Fact.Create(FactPaths.DockerContainersPaused, keys, root.GetInt("ContainersPaused")));
        facts.Add(Fact.Create(FactPaths.DockerContainersStopped, keys, root.GetInt("ContainersStopped")));
        facts.Add(Fact.Create(FactPaths.DockerImages, keys, root.GetInt("Images")));
        facts.Add(Fact.Create(FactPaths.DockerOS, keys, root.GetStr("OperatingSystem") ?? ""));
        facts.Add(Fact.Create(FactPaths.DockerKernel, keys, root.GetStr("KernelVersion") ?? ""));
        facts.Add(Fact.Create(FactPaths.DockerMemBytes, keys, root.GetLong("MemTotal")));
        facts.Add(Fact.Create(FactPaths.DockerCpuCount, keys, root.GetInt("NCPU")));
    }

    private static async Task CollectContainersAsync(
        string deviceId,
        HttpClient http,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // all=true to include stopped containers
        HttpResponseMessage resp = await http.GetAsync("/v1.43/containers/json?all=true", ct);
        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct
        );

        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            // Use the short container ID as the key dimension
            string id = (c.GetStr("Id") ?? "")[..12];
            string[] containerKeys = [deviceId, id];

            // First name without leading /
            string names = c.TryGetProperty("Names", out JsonElement n) && n.GetArrayLength() > 0
                ? n[0].GetString()?.TrimStart('/') ?? id
                : id;

            facts.Add(Fact.Create(FactPaths.ContainerName, containerKeys, names));
            facts.Add(Fact.Create(FactPaths.ContainerImage, containerKeys, c.GetStr("Image") ?? ""));
            facts.Add(Fact.Create(FactPaths.ContainerStatus, containerKeys, c.GetStr("Status") ?? ""));
            facts.Add(Fact.Create(FactPaths.ContainerState, containerKeys, c.GetStr("State") ?? ""));
            facts.Add(Fact.Create(FactPaths.ContainerCreated, containerKeys, c.GetLong("Created")));

            string ports = FormatPorts(c);
            if (ports.Length > 0)
            {
                facts.Add(Fact.Create(FactPaths.ContainerPorts, containerKeys, ports));
            }

            string mounts = FormatMounts(c);
            if (mounts.Length > 0)
            {
                facts.Add(Fact.Create(FactPaths.ContainerMounts, containerKeys, mounts));
            }

            string labels = FormatLabels(c);
            if (labels.Length > 0)
            {
                facts.Add(Fact.Create(FactPaths.ContainerLabels, containerKeys, labels));
            }

            // TODO: fetch per-container stats via /v1.43/containers/{id}/stats?stream=false
            // for CPU%, memory usage, network I/O. Requires one HTTP call per container.
        }
    }

    // One fact-group per IPAM subnet, keyed by the subnet CIDR. The subnet is the L3
    // entity the Subnets page joins on; a bridge network's driver/scope is what tells it
    // the CIDR is host-local NAT (and so must be keyed per-host, not merged globally).
    // Networks with no IPAM subnet (host/none) produce no rows — nothing to place on L3.
    private static async Task CollectNetworksAsync(
        string deviceId,
        HttpClient http,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        HttpResponseMessage resp = await http.GetAsync("/v1.43/networks", ct);
        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct
        );

        facts.AddRange(ParseNetworks(deviceId, doc.RootElement));
    }

    // Parses a Docker `GET /networks` response array into per-subnet facts. Split out from the
    // HTTP fetch so the parsing is unit-testable against sample payloads. Tolerant of the shapes
    // real daemons emit: a non-array root, networks with no IPAM/Config (host/none), empty subnet
    // strings, and a missing bridge-name option all degrade to "emit less", never throw.
    internal static IReadOnlyList<Fact> ParseNetworks(string deviceId, JsonElement root)
    {
        List<Fact> facts = new();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return facts;
        }

        foreach (JsonElement net in root.EnumerateArray())
        {
            if (net.ValueKind != JsonValueKind.Object
             || !net.TryGetProperty("IPAM", out JsonElement ipam)
             || ipam.ValueKind != JsonValueKind.Object
             || !ipam.TryGetProperty("Config", out JsonElement config)
             || config.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            string name = net.GetStr(nameof(Name)) ?? "";
            string driver = net.GetStr("Driver") ?? "";
            string scope = net.GetStr("Scope") ?? "";
            string bridge = BridgeName(net);

            foreach (JsonElement entry in config.EnumerateArray())
            {
                string? subnet = entry.ValueKind == JsonValueKind.Object ? entry.GetStr("Subnet") : null;
                if (string.IsNullOrWhiteSpace(subnet))
                {
                    continue;
                }

                string[] keys = [deviceId, subnet];
                facts.Add(Fact.Create(FactPaths.DockerNetworkName, keys, name));
                facts.Add(Fact.Create(FactPaths.DockerNetworkDriver, keys, driver));
                facts.Add(Fact.Create(FactPaths.DockerNetworkScope, keys, scope));
                if (bridge.Length > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DockerNetworkBridge, keys, bridge));
                }
            }
        }

        return facts;
    }

    // Options["com.docker.network.bridge.name"] ties a bridge network's CIDR back to its
    // host interface: "docker0" for the default bridge, "br-<hash>" for user-defined ones.
    private static string BridgeName(JsonElement net)
    {
        if (!net.TryGetProperty("Options", out JsonElement options) || options.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return options.TryGetProperty("com.docker.network.bridge.name", out JsonElement br)
            ? br.GetString() ?? ""
            : "";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static HttpClient GetOrCreateWindowsClient() =>
        _clients.GetOrAdd("win://pipe/docker_engine", _ => BuildWindowsClient());

    private static HttpClient BuildUnixClient(string socketPath)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                Socket sock = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(sock, ownsSocket: true);
            },
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    [SupportedOSPlatform("windows")]
    private static HttpClient BuildWindowsClient()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                NamedPipeClientStream pipe = new(
                    ".",
                    "docker_engine",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous
                );
                await pipe.ConnectAsync(5000, ct);
                return pipe;
            },
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    // "0.0.0.0:8080->80/tcp, :::8080->80/tcp" — only published (PublicPort) mappings.
    private static string FormatPorts(JsonElement c)
    {
        if (!c.TryGetProperty("Ports", out JsonElement ports) || ports.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        List<string> parts = new();
        foreach (JsonElement p in ports.EnumerateArray())
        {
            if (!p.TryGetProperty("PublicPort", out JsonElement pub) || pub.ValueKind != JsonValueKind.Number)
            {
                continue; // unpublished — internal only
            }

            string ip = p.GetStr("IP") ?? "";
            long priv = p.GetLong("PrivatePort");
            string type = p.GetStr("Type") ?? "";
            string host = ip.Length > 0 ? $"{ip}:{pub.GetInt64()}" : pub.GetInt64().ToString();
            parts.Add($"{host}->{priv}/{type}");
        }

        return string.Join(", ", parts.Distinct());
    }

    // "src:dst, volname:/data" — bind + volume mounts.
    private static string FormatMounts(JsonElement c)
    {
        if (!c.TryGetProperty("Mounts", out JsonElement mounts) || mounts.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        List<string> parts = new();
        foreach (JsonElement m in mounts.EnumerateArray())
        {
            string src = m.GetStr("Source") ?? "";
            string name = m.GetStr(nameof(Name)) ?? "";
            string dst = m.GetStr("Destination") ?? "";
            string from = src.Length > 0 ? src : name;
            if (dst.Length > 0)
            {
                parts.Add(from.Length > 0 ? $"{from}:{dst}" : dst);
            }
        }

        return string.Join(", ", parts);
    }

    // "com.docker.compose.project=web, maintainer=..." — first 15 labels.
    private static string FormatLabels(JsonElement c)
    {
        if (!c.TryGetProperty("Labels", out JsonElement labels) || labels.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        List<string> parts = new();
        foreach (JsonProperty kv in labels.EnumerateObject())
        {
            if (parts.Count >= 15)
            {
                break;
            }

            parts.Add($"{kv.Name}={kv.Value.GetString() ?? ""}");
        }

        return string.Join(", ", parts);
    }
}

internal static partial class DockerCollectorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Docker collection error.")]
    public static partial void CollectionError(ILogger logger, Exception ex);
}