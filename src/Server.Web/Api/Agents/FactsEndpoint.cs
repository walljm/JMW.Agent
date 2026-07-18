using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// POST /api/v1/agent/facts — receives gzip-compressed JSON from approved agents.
/// One request covers all devices collected in a single cycle.
/// </summary>
public static class FactsEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxDecompressedBytes = 50 * 1024 * 1024; // 50 MB
    private const int MaxBodyBytes = MaxDecompressedBytes;
    private const int MaxFactStringValueLength = 4096;

    // Attribute paths (Service dimension key stripped) used to inspect a rewritten service batch
    // for an existing host link and the agent-reported endpoint address.
    private static readonly string ServiceDeviceIdAttr =
        ServicePaths.DeviceId.Replace("[]", "", StringComparison.Ordinal);

    private static readonly string ServiceAddressAttr =
        ServicePaths.Address.Replace("[]", "", StringComparison.Ordinal);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/facts", HandleAsync).RequireRateLimiting("agent-facts");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        NpgsqlDataSource db,
        FactIngestPipeline pipeline,
        DiscoveryMaterializer materializer,
        ServiceRegistry serviceRegistry,
        AuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        // AgentApiKeyMiddleware already ran — claims are set if the key was valid.
        string? agentIdClaim = context.User.FindFirstValue("agent_id");
        if (string.IsNullOrEmpty(agentIdClaim) || !Guid.TryParse(agentIdClaim, out Guid authenticatedAgentId))
        {
            return ErrorResult(401, "unauthorized", "Invalid API key.");
        }

        // Decompress gzip body. The body stream is read once here — nothing upstream
        // buffers or pre-reads it, so it does not need to be seekable.
        AgentFactsRequest request;
        try
        {
            Stream bodyStream = context.Request.Body;
            string contentEncoding = context.Request.Headers.ContentEncoding.ToString();
            if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                await using GZipStream gz = new(bodyStream, CompressionMode.Decompress, leaveOpen: true);
                await using BoundedStream bounded = new(gz, MaxDecompressedBytes);
                request = await JsonSerializer.DeserializeAsync<AgentFactsRequest>(bounded, JsonOpts, ct)
                 ?? throw new InvalidOperationException("Empty request body.");
            }
            else
            {
                await using BoundedStream bounded = new(bodyStream, MaxBodyBytes);
                request = await JsonSerializer.DeserializeAsync<AgentFactsRequest>(bounded, JsonOpts, ct)
                 ?? throw new InvalidOperationException("Empty request body.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        {
            return ErrorResult(400, "invalid_body", "Request body could not be parsed.");
        }

        // Verify the agent_id in the body matches the authenticated agent.
        if (request.AgentId != authenticatedAgentId)
        {
            return ErrorResult(403, "agent_id_mismatch", "agent_id does not match authenticated agent.");
        }

        // Check agent is approved — middleware does not enforce this.
        string? agentStatus;
        await using (NpgsqlConnection conn = await db.OpenConnectionAsync(ct))
        {
            AgentStatusResult statusResult =
                await conn.GetAgentStatusAsync(authenticatedAgentId, ct).FirstOrDefaultAsync(ct);
            agentStatus = statusResult.Status;
        }

        if (agentStatus is null)
        {
            return ErrorResult(404, "not_found", "Agent not found.");
        }

        if (agentStatus != "approved")
        {
            await audit.WriteAsync(
                $"agent:{authenticatedAgentId}",
                "facts.rejected.not_approved",
                authenticatedAgentId.ToString(),
                ct: ct
            );
            return ErrorResult(403, "not_approved", "Agent is not approved.");
        }

        // If no fact batches but cycle summary present: store summary and return early.
        if (request.FactBatches.Count == 0)
        {
            if (request.CycleSummary is null)
            {
                return ErrorResult(400, "no_batches", "Request contains no fact batches.");
            }

            await StoreCycleAsync(db, authenticatedAgentId, request.CollectedAt, request.CycleSummary, ct);
            return Results.Json(new AgentFactsResponse(0, []), JsonOpts, statusCode: 202);
        }

        int totalFacts = request.FactBatches.Sum(b => b.Facts.Count);

        if (totalFacts > FactIngestPipeline.MaxFactsPerBatch)
        {
            return ErrorResult(
                413,
                "too_many_facts",
                $"Total facts {totalFacts} exceeds maximum of {FactIngestPipeline.MaxFactsPerBatch}."
            );
        }

        // Process each batch element.
        List<ResolvedDevice> resolvedDevices = new(request.FactBatches.Count);
        int acceptedBatches = 0;
        HashSet<string> touchedTables = new(StringComparer.Ordinal);

        // One connection for all ResolveWithConnectionAsync + StampAgentDevice calls — avoids N+1 opens.
        await using NpgsqlConnection batchConn = await db.OpenConnectionAsync(ct);

        foreach (FactBatchElement batchElement in request.FactBatches)
        {
            // Service batches carry an identity probe instead of device fingerprints.
            // Resolve (or mint) a stable ServiceId from the probe's logical
            // fingerprints and ingest the facts under the Service[{serviceId}] root.
            if (batchElement.Service is { } probe)
            {
                if (string.IsNullOrWhiteSpace(probe.ServiceType) || probe.Fingerprints.Count == 0)
                {
                    return ErrorResult(
                        400,
                        "invalid_service_probe",
                        "A service batch element has no service type or fingerprints."
                    );
                }

                (string serviceId, bool isNewService) = await serviceRegistry.IdentifyAsync(
                    new ServiceIdentifyRequest(authenticatedAgentId.ToString(), probe),
                    ct
                );

                if (batchElement.Facts.Count > 0)
                {
                    List<Fact> serviceFacts = RewriteServiceFactIds(
                        batchElement.Facts,
                        serviceId,
                        request.CollectedAt,
                        authenticatedAgentId
                    );

                    // Self-referential ServiceId fact so projections keyed on the
                    // Service dimension carry the id as a queryable column too.
                    serviceFacts.Add(
                        Fact.Create(ServicePaths.ServiceId, [serviceId], serviceId, request.CollectedAt)
                    );

                    // Link the service to its host device. Loopback services already carry a
                    // Service[].DeviceId fact; a remotely-polled service instead sent its endpoint
                    // IP (Service[].Address) — resolve that to the hosting device here, since the
                    // agent can't know server-assigned DeviceIds. Never overrides an existing link.
                    await LinkServiceHostAsync(batchConn, serviceFacts, serviceId, request.CollectedAt, ct);

                    touchedTables.UnionWith(await pipeline.IngestAsync(serviceFacts, ct));

                    // HA's registry describes OTHER devices, fully reported by one collector in
                    // one cycle — resolve/promote them inline, off this same in-memory list,
                    // rather than via DiscoveryMaterializer (see docs/plans/ha-inline-discovery.md).
                    if (probe.ServiceType == "home-assistant")
                    {
                        ILogger promotionLogger = loggerFactory.CreateLogger(typeof(HomeAssistantDevicePromotion));
                        await HomeAssistantDevicePromotion.PromoteAsync(
                            batchConn,
                            pipeline,
                            serviceFacts,
                            promotionLogger,
                            ct
                        );
                    }
                }

                ServiceFingerprint first = probe.Fingerprints[0];
                resolvedDevices.Add(new ResolvedDevice($"{first.Type}:{first.Value}", serviceId, isNewService));
                acceptedBatches++;
                continue;
            }

            // Normalize fingerprints and reject batch if none valid.
            List<Fingerprint> normalized = DeviceRegistry.NormalizeAll(batchElement.Fingerprints);
            if (normalized.Count == 0)
            {
                return ErrorResult(400, "no_valid_fingerprints", "A batch element has no valid fingerprints.");
            }

            string fingerprintsHash = ComputeFingerprintsHash(normalized);

            (string deviceId, bool isNew) = await DeviceRegistry.ResolveWithConnectionAsync(
                batchConn,
                normalized,
                source: "agent",
                managementStatus: "managed",
                ct: ct
            );

            // Rewrite fact IDs: replace the placeholder root with Device[{deviceId}].
            // Must use Fact.Create() to keep all derived fields (AttributePath, KeyValuesJson, etc.) consistent.
            if (batchElement.Facts.Count > 0)
            {
                List<Fact> rewrittenFacts = RewriteFactIds(
                    batchElement.Facts,
                    deviceId,
                    request.CollectedAt,
                    authenticatedAgentId
                );
                touchedTables.UnionWith(
                    await pipeline.IngestAsync(rewrittenFacts, ct)
                );
            }

            // Stamp the resolved device_id onto the agent record (first batch only — no-op after that).
            if (Guid.TryParse(deviceId, out Guid deviceGuid))
            {
                await batchConn.StampAgentDeviceAsync(authenticatedAgentId, deviceGuid, ct).ExecuteAsync(ct);
            }

            resolvedDevices.Add(new ResolvedDevice(fingerprintsHash, deviceId, isNew));
            acceptedBatches++;
        }

        // Post-ingest discovery pass — run after all readers are closed. Only when this batch
        // actually touched a projection DiscoveryMaterializer reads from (performance-03) — a
        // services-only POST, or a discovery-cadence tick that found nothing new, can't have
        // changed what any of its passes would find.
        if (touchedTables.Overlaps(DiscoveryMaterializer.RelevantTables))
        {
            await materializer.MaterializeAsync(ct);
        }

        // Store cycle summary if agent sent one.
        if (request.CycleSummary is not null)
        {
            await StoreCycleAsync(db, authenticatedAgentId, request.CollectedAt, request.CycleSummary, ct);
        }

        return Results.Json(
            new AgentFactsResponse(acceptedBatches, resolvedDevices),
            JsonOpts,
            statusCode: 202
        );
    }

    private static async Task StoreCycleAsync(
        NpgsqlDataSource db,
        Guid agentId,
        DateTimeOffset cycleAt,
        AgentCycleSummary cycle,
        CancellationToken ct
    )
    {
        int errorCount = (cycle.Collectors?.Count(c => c.Error != null) ?? 0)
          + (cycle.Scanners?.Count(s => s.Error != null) ?? 0)
          + (cycle.DeviceScanners?.Count(d => d.Error != null) ?? 0)
          + (cycle.Services?.Count(s => s.Error != null) ?? 0);

        JsonElement collectorsJson = JsonSerializer.SerializeToElement(
            cycle.Collectors ?? (IReadOnlyList<CollectorStat>)[],
            JsonOpts
        );
        JsonElement scannersJson = JsonSerializer.SerializeToElement(
            cycle.Scanners ?? (IReadOnlyList<ScannerStat>)[],
            JsonOpts
        );
        JsonElement deviceScannersJson = JsonSerializer.SerializeToElement(
            cycle.DeviceScanners ?? (IReadOnlyList<DeviceScannerStat>)[],
            JsonOpts
        );
        JsonElement servicesJson = JsonSerializer.SerializeToElement(
            cycle.Services ?? (IReadOnlyList<ServiceStat>)[],
            JsonOpts
        );

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await conn.InsertAgentCycleAsync(
                agentId,
                cycleAt,
                cycle.DurationMs,
                cycle.FactsSent,
                errorCount,
                collectorsJson,
                scannersJson,
                deviceScannersJson,
                servicesJson,
                ct
            )
            .ExecuteAsync(ct);
    }

    /// <summary>
    /// Rewrites the placeholder root segment (whatever comes before the first '.') to
    /// Device[{deviceId}]. Uses Fact.Create() so all derived fields stay consistent.
    /// </summary>
    private static List<Fact> RewriteFactIds(
        IReadOnlyList<Fact> facts,
        string deviceId,
        DateTimeOffset collectedAt,
        Guid agentId
    )
    {
        List<Fact> result = new(facts.Count);

        foreach (Fact fact in facts)
        {
            result.Add(
                Fact.Create(RewriteRootKey(fact.Id, deviceId), ClampStringValue(fact.Value), collectedAt) with
                {
                    Source = fact.Source,
                    AgentId = agentId,
                }
            );
        }

        return result;
    }

    /// <summary>
    /// Rewrites the placeholder Service[...] root segment to Service[{serviceId}].
    /// </summary>
    /// <summary>
    /// Sets <c>Service[].DeviceId</c> from the agent-reported endpoint IP (<c>Service[].Address</c>)
    /// when the batch doesn't already link the service to a host — loopback services link directly,
    /// so this only fills remotely-polled ones. Resolves the IP to a live device and leaves the
    /// service unlinked when it maps to no known device (never guesses). Appends the DeviceId fact
    /// to <paramref name="serviceFacts" /> in place.
    /// </summary>
    private static async Task LinkServiceHostAsync(
        NpgsqlConnection conn,
        List<Fact> serviceFacts,
        string serviceId,
        DateTimeOffset collectedAt,
        CancellationToken ct
    )
    {
        if (serviceFacts.Any(f => f.AttributePath == ServiceDeviceIdAttr))
        {
            return; // already linked (loopback / agent-provided)
        }

        string? endpointIp = null;
        foreach (Fact fact in serviceFacts)
        {
            if (fact.AttributePath == ServiceAddressAttr)
            {
                endpointIp = fact.Value.AsString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(endpointIp))
        {
            return;
        }

        ServiceHostDevice? host = await conn.ResolveServiceHostDeviceAsync(endpointIp, ct)
            .FirstOrDefaultAsync(ct);
        if (host?.DeviceId is { } deviceId)
        {
            serviceFacts.Add(Fact.Create(ServicePaths.DeviceId, [serviceId], deviceId, collectedAt));
        }
    }

    private static List<Fact> RewriteServiceFactIds(
        IReadOnlyList<Fact> facts,
        string serviceId,
        DateTimeOffset collectedAt,
        Guid agentId
    )
    {
        List<Fact> result = new(facts.Count + 1);

        foreach (Fact fact in facts)
        {
            result.Add(
                Fact.Create(RewriteRootKey(fact.Id, serviceId), ClampStringValue(fact.Value), collectedAt) with
                {
                    Source = fact.Source,
                    AgentId = agentId,
                }
            );
        }

        return result;
    }

    /// <summary>
    /// Replaces the bracket key of a fact id's root segment (e.g. the agent's placeholder
    /// "Device[]"/"Service[...]") with <paramref name="newKey" />, keeping the rest of the path
    /// intact. Routes through the canonical <see cref="FactSegment.ParsePath" /> tokenizer (review
    /// D25) rather than an ad-hoc <c>IndexOf</c> search — the root's own bracket key can contain
    /// dots (zone names, URLs), which a naive first-'.' search gets wrong.
    /// </summary>
    private static string RewriteRootKey(string id, string newKey)
    {
        FactSegment[] segments = FactSegment.ParsePath(id);
        if (segments.Length == 0)
        {
            return id;
        }

        segments[0] = segments[0] with { Key = newKey };
        return string.Join('.', segments.Select(s => s.ToString()));
    }

    private static FactValue ClampStringValue(FactValue value)
    {
        if (value.Kind == FactValueKind.String)
        {
            string? s = value.AsString();
            if (s != null && s.Length > MaxFactStringValueLength)
            {
                return FactValue.FromString(s[..MaxFactStringValueLength]);
            }
        }

        return value;
    }

    private static string ComputeFingerprintsHash(List<Fingerprint> fingerprints)
    {
        // Deterministic hash: sort by type+value so order doesn't affect the key.
        string canonical = string.Join(
            "|",
            fingerprints
                .OrderBy(f => f.Type)
                .ThenBy(f => f.Value)
                .Select(f => $"{f.Type}:{f.Value}")
        );
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static IResult ErrorResult(int status, string code, string message) =>
        ApiError.Problem(status, code, message);

    // Wraps a stream and throws if more than maxBytes are read through it.
    // Eliminates the intermediate MemoryStream for decompressed bodies.
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxBytes;

        public BoundedStream(Stream inner, int maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        private int _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            _totalRead += read;
            if (_totalRead > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"Payload exceeds {_maxBytes / 1024 / 1024} MB limit."
                );
            }

            return read;
        }
    }
}