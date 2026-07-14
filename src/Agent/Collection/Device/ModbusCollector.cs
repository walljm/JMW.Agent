using System.Net.Sockets;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Collects register values from a Modbus TCP device. Targets must have
/// CollectorType = "modbus". Reads configurable ranges of holding registers and input
/// registers. Register semantics are vendor-specific; values are emitted as raw uint16
/// facts. No authentication — read-only operations only.
/// </summary>
public sealed class ModbusCollector : IDeviceCollector
{
    private readonly ILogger<ModbusCollector> _logger = AgentLog.CreateLogger<ModbusCollector>();
    private const int ModbusPort = 502;
    private const int DefaultTimeoutMs = 3000;

    public string CollectorType => "modbus";

    public bool CanCollect(Target target) =>
        target.CollectorType != null && target.CollectorType.Equals("modbus", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    )
    {
        int port = target.Properties.GetInt("port", ModbusPort);
        int timeoutMs = target.Properties.GetInt("timeout_ms", DefaultTimeoutMs);
        byte unitId = target.Properties.GetByte("unit_id", 1);
        ushort holdingStart = target.Properties.GetUShort("holding_start", 0);
        ushort holdingCount = target.Properties.GetUShort("holding_count", 10);
        ushort inputStart = target.Properties.GetUShort("input_start", 0);
        ushort inputCount = target.Properties.GetUShort("input_count", 10);

        try
        {
            using TcpClient tcp = new();
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;

            using CancellationTokenSource timeoutCts = new(timeoutMs);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

            await tcp.ConnectAsync(target.Endpoint, port, linked.Token);
            NetworkStream stream = tcp.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            ushort txId = 1;

            // Attempt FC 43 MEI Type 14 Device Identification — best available stable fingerprint.
            // Supported on ~30-50% of Modbus TCP devices; silently ignored if not supported.
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            Dictionary<byte, string>? mei = await TryReadMeiIdentificationAsync(stream, txId, unitId, _logger, ct);
            txId++;

            // Read holding registers (FC 03)
            ushort[] holding = await ReadRegistersAsync(
                stream,
                ModbusClient.BuildReadHoldingRegisters(txId, unitId, holdingStart, holdingCount),
                txId,
                _logger,
                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                ct
            );
            txId++;

            // Read input registers (FC 04)
            ushort[] input = await ReadRegistersAsync(
                stream,
                ModbusClient.BuildReadInputRegisters(txId, unitId, inputStart, inputCount),
                txId,
                _logger,
                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                ct
            );

            // Build fingerprint: MEI product identity is preferred (vendor-scoped, durable).
            // Fall back to register values, then IP+port+unit if nothing is available.
            List<Fingerprint> fingerprints = new(1);
            string? meiVendor = null;
            string? meiProduct = null;
            mei?.TryGetValue(0x00, out meiVendor);
            mei?.TryGetValue(0x01, out meiProduct);

            if (!string.IsNullOrWhiteSpace(meiVendor) && !string.IsNullOrWhiteSpace(meiProduct))
            {
                string? normalized = FingerprintNormalizer.NormalizeModbusMeiProduct(
                    $"{meiVendor}:{meiProduct}"
                );
                if (normalized is not null)
                {
                    fingerprints.Add(new Fingerprint(FingerprintType.ModbusMeiProduct, normalized));
                }
            }

            if (fingerprints.Count == 0 && holding.Length >= 2)
            {
                // Register-based fallback: not semantically stable but unique within a site.
                string fpValue = $"{target.Endpoint}:{port}:u{unitId}:{holding[0]:X4}{holding[1]:X4}";
                fingerprints.Add(new Fingerprint(FingerprintType.ModbusMeiProduct, fpValue));
            }

            if (fingerprints.Count == 0)
            {
                fingerprints.Add(
                    new Fingerprint(
                        FingerprintType.ModbusMeiProduct,
                        $"{target.Endpoint}:{port}:u{unitId}"
                    )
                );
            }

            string? vendor = string.IsNullOrWhiteSpace(meiVendor)
                ? null
                : meiVendor.ToLowerInvariant().Split(' ')[0];

            DeviceIdentity identity = new(
                Fingerprints: fingerprints,
                Kind: "industrial-iot",
                Vendor: vendor,
                OsFamily: null,
                OsVersion: null
            );

            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            string deviceId = await context.RegisterProbeAsync(identity, ct);

            List<Fact> facts = new(3 + holding.Length + input.Length);

            // MEI identification facts
            if (mei is not null)
            {
                if (mei.TryGetValue(0x00, out string? vendorName) && !string.IsNullOrEmpty(vendorName))
                {
                    facts.Add(Fact.Create(FactPaths.ModbusVendorName, [deviceId], vendorName));
                }

                if (mei.TryGetValue(0x01, out string? productCode) && !string.IsNullOrEmpty(productCode))
                {
                    facts.Add(Fact.Create(FactPaths.ModbusProductCode, [deviceId], productCode));
                }

                if (mei.TryGetValue(0x02, out string? revision) && !string.IsNullOrEmpty(revision))
                {
                    facts.Add(Fact.Create(FactPaths.ModbusRevision, [deviceId], revision));
                }
            }

            for (int i = 0; i < holding.Length; i++)
            {
                string addr = (holdingStart + i).ToString();
                facts.Add(Fact.Create(FactPaths.ModbusHoldingRegister, [deviceId, addr], holding[i]));
            }

            for (int i = 0; i < input.Length; i++)
            {
                string addr = (inputStart + i).ToString();
                facts.Add(Fact.Create(FactPaths.ModbusInputRegister, [deviceId, addr], input[i]));
            }

            return facts;
        }
        catch (SocketException ex)
        {
            SocketError socketError = ex.SocketErrorCode;
            ModbusCollectorLog.SocketError(_logger, target.Endpoint, socketError, ex);
            return Array.Empty<Fact>();
        }
        catch (Exception ex)
        {
            ModbusCollectorLog.CollectionFailed(_logger, target.Endpoint, ex);
            return Array.Empty<Fact>();
        }
    }

    private static async Task<Dictionary<byte, string>?> TryReadMeiIdentificationAsync(
        NetworkStream stream,
        ushort transactionId,
        byte unitId,
        ILogger<ModbusCollector> logger,
        CancellationToken ct
    )
    {
        try
        {
            byte[] request = ModbusClient.BuildReadDeviceIdentification(transactionId, unitId);
            await stream.WriteAsync(request.AsMemory(), ct);

            byte[] buffer = new byte[512];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read < 14)
            {
                return null;
            }

            return ModbusClient.ParseDeviceIdentification(buffer[..read], transactionId);
        }
        catch (Exception ex)
        {
            ModbusCollectorLog.MeiIdentificationFailed(logger, ex);
            return null;
        }
    }

    private static async Task<ushort[]> ReadRegistersAsync(
        NetworkStream stream,
        byte[] request,
        ushort transactionId,
        ILogger<ModbusCollector> logger,
        CancellationToken ct
    )
    {
        try
        {
            await stream.WriteAsync(request.AsMemory(), ct);

            byte[] buffer = new byte[512];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read < 7)
            {
                return [];
            }

            (byte _, byte[] data)? result = ModbusClient.ParseResponse(buffer[..read], transactionId);
            return result is null ? [] : ModbusClient.DecodeRegisters(result.Value.data);
        }
        catch (Exception ex)
        {
            ModbusCollectorLog.RegisterReadFailed(logger, ex);
            return [];
        }
    }
}

internal static partial class ModbusCollectorLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Modbus socket error collecting {Address}: {SocketError}."
    )]
    internal static partial void SocketError(
        ILogger logger,
        string address,
        SocketError socketError,
        SocketException ex
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Modbus collection failed for {Address}."
    )]
    internal static partial void CollectionFailed(ILogger logger, string address, Exception ex);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Modbus MEI device identification failed."
    )]
    internal static partial void MeiIdentificationFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Modbus register read failed."
    )]
    internal static partial void RegisterReadFailed(ILogger logger, Exception ex);
}