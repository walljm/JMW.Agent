using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Collects device identity and status from a BACnet/IP device via repeated
/// ReadProperty requests to the Device object. Targets must have CollectorType = "bacnet".
/// No authentication — read-only operations only.
/// </summary>
public sealed class BacnetCollector : IDeviceCollector
{
    private readonly ILogger<BacnetCollector> _logger = AgentLog.CreateLogger<BacnetCollector>();
    private const int DefaultPort = 47808;
    private const int DefaultTimeoutMs = 5000;

    public string CollectorType => "bacnet";

    public bool CanCollect(Target target) =>
        target.CollectorType != null && target.CollectorType.Equals("bacnet", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    )
    {
        int port = target.Properties.GetInt("port", DefaultPort);
        int timeoutMs = target.Properties.GetInt("timeout_ms", DefaultTimeoutMs);

        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMs);

        try
        {
            using UdpClient udp = new();
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Connect(new IPEndPoint(IPAddress.Parse(target.Endpoint), port));

            // First request: object identifier — confirms device is reachable and gives device instance.
            byte[]? objectIdResponse = await BacnetClient.SendReceiveAsync(
                udp,
                BacnetClient.BuildReadPropertyRequest(BacnetPropertyId.ObjectIdentifier),
                timeout,
                ct
            );

            if (objectIdResponse is null)
            {
                BacnetCollectorLog.Timeout(_logger, target.Endpoint, port);
                return Array.Empty<Fact>();
            }

            uint? deviceInstance = ExtractDeviceInstance(objectIdResponse, _logger);

            // Collect identity properties sequentially.
            string? vendorName = await QueryStringAsync(udp, BacnetPropertyId.VendorName, timeout, _logger, ct);
            uint? vendorIdNum = await QueryUIntAsync(udp, BacnetPropertyId.VendorIdentifier, timeout, _logger, ct);
            string? vendorId = vendorIdNum?.ToString();
            string? modelName = await QueryStringAsync(udp, BacnetPropertyId.ModelName, timeout, _logger, ct);
            string? objectName = await QueryStringAsync(udp, BacnetPropertyId.ObjectName, timeout, _logger, ct);
            string? firmware = await QueryStringAsync(udp, BacnetPropertyId.FirmwareRevision, timeout, _logger, ct);
            string? appVersion = await QueryStringAsync(
                udp,
                BacnetPropertyId.ApplicationSoftwareVersion,
                timeout,
                _logger,
                ct
            );
            string? description = await QueryStringAsync(udp, BacnetPropertyId.Description, timeout, _logger, ct);
            string? location = await QueryStringAsync(udp, BacnetPropertyId.Location, timeout, _logger, ct);
            string? status = await QueryStringAsync(udp, BacnetPropertyId.SystemStatus, timeout, _logger, ct);
            string? serial = await QueryStringAsync(udp, BacnetPropertyId.SerialNumber, timeout, _logger, ct);

            // Primary fingerprint: (VendorId, DeviceInstance) — globally unique per ASHRAE 135.
            // Fall back to bare device instance if vendor ID unavailable (site-unique only).
            List<Fingerprint> fingerprints = new(2);
            if (deviceInstance.HasValue)
            {
                string fpValue = vendorIdNum.HasValue
                    ? $"{vendorIdNum.Value}:{deviceInstance.Value}"
                    : $"0:{deviceInstance.Value}";
                string? normalized = FingerprintNormalizer.NormalizeBacnetVendorInstance(fpValue);
                if (normalized is not null)
                {
                    fingerprints.Add(new Fingerprint(FingerprintType.BacnetVendorInstance, normalized));
                }
            }

            // Secondary fingerprint: serial number scoped to vendor (if available).
            if (!string.IsNullOrWhiteSpace(serial) && !string.IsNullOrEmpty(vendorName))
            {
                string? normalized = FingerprintNormalizer.NormalizeSerial(serial, vendorName);
                if (normalized is not null)
                {
                    fingerprints.Add(new Fingerprint(FingerprintType.ChassisSerial, normalized));
                }
            }

            if (fingerprints.Count == 0)
            {
                BacnetCollectorLog.NoFingerprints(_logger, target.Endpoint);
                return Array.Empty<Fact>();
            }

            string? vendor = string.IsNullOrWhiteSpace(vendorName)
                ? null
                : vendorName.ToLowerInvariant().Split(' ')[0]; // e.g. "Siemens AG" → "siemens"

            DeviceIdentity identity = new(
                Fingerprints: fingerprints,
                Kind: "building-automation",
                Vendor: vendor,
                OsFamily: null,
                OsVersion: null
            );

            string deviceId = await context.RegisterProbeAsync(identity, ct);

            List<Fact> facts = new(12);

            if (deviceInstance.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.BacnetDeviceInstance, [deviceId], deviceInstance.Value));
            }

            facts.AddIfPresent(FactPaths.BacnetVendorName, [deviceId], vendorName);

            facts.AddIfPresent(FactPaths.BacnetVendorId, [deviceId], vendorId);

            facts.AddIfPresent(FactPaths.BacnetModelName, [deviceId], modelName);

            facts.AddIfPresent(FactPaths.BacnetObjectName, [deviceId], objectName);

            facts.AddIfPresent(FactPaths.BacnetFirmwareRevision, [deviceId], firmware);

            facts.AddIfPresent(FactPaths.BacnetApplicationSoftwareVersion, [deviceId], appVersion);

            facts.AddIfPresent(FactPaths.BacnetDescription, [deviceId], description);

            facts.AddIfPresent(FactPaths.BacnetLocation, [deviceId], location);

            facts.AddIfPresent(FactPaths.BacnetSystemStatus, [deviceId], status);

            facts.AddIfPresent(FactPaths.BacnetSerialNumber, [deviceId], serial);

            return facts;
        }
        catch (SocketException ex)
        {
            SocketError socketError = ex.SocketErrorCode;
            BacnetCollectorLog.SocketError(_logger, target.Endpoint, socketError, ex);
            return Array.Empty<Fact>();
        }
        catch (Exception ex)
        {
            BacnetCollectorLog.CollectionFailed(_logger, target.Endpoint, ex);
            return Array.Empty<Fact>();
        }
    }

    private static async ValueTask<uint?> QueryUIntAsync(
        UdpClient udp,
        BacnetPropertyId property,
        TimeSpan timeout,
        ILogger<BacnetCollector> logger,
        CancellationToken ct
    )
    {
        byte[]? response = await BacnetClient.SendReceiveAsync(
            udp,
            BacnetClient.BuildReadPropertyRequest(property),
            timeout,
            ct
        );

        if (response is null)
        {
            return null;
        }

        try
        {
            ReadOnlySpan<byte> apdu = BacnetClient.StripBvllAndNpdu(response);
            return BacnetClient.ExtractUnsignedValue(apdu);
        }
        catch (Exception ex)
        {
            BacnetCollectorLog.UIntParseFailed(logger, ex);
            return null;
        }
    }

    private static async ValueTask<string?> QueryStringAsync(
        UdpClient udp,
        BacnetPropertyId property,
        TimeSpan timeout,
        ILogger<BacnetCollector> logger,
        CancellationToken ct
    )
    {
        byte[]? response = await BacnetClient.SendReceiveAsync(
            udp,
            BacnetClient.BuildReadPropertyRequest(property),
            timeout,
            ct
        );

        if (response is null)
        {
            return null;
        }

        try
        {
            ReadOnlySpan<byte> apdu = BacnetClient.StripBvllAndNpdu(response);
            string? value = BacnetClient.ExtractStringValue(apdu);
            // Empty string = device responded "Reject" (property not supported) — treat as null.
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
        {
            BacnetCollectorLog.StringParseFailed(logger, ex);
            return null;
        }
    }

    private static uint? ExtractDeviceInstance(byte[] response, ILogger<BacnetCollector> logger)
    {
        try
        {
            ReadOnlySpan<byte> apdu = BacnetClient.StripBvllAndNpdu(response);
            int pduType = apdu[0] & 0xF0;
            if (pduType is 0x50 or 0x60 or 0x70)
            {
                return null;
            }

            if (apdu.Length < 8)
            {
                return null;
            }

            // Object identifier starts at byte 4 of the APDU (after type, invoke-id, service, tag).
            // Take bytes 5–7 (skip the type byte) to get the 24-bit instance portion.
            Span<byte> buf = stackalloc byte[4];
            apdu.Slice(5, 3).CopyTo(buf.Slice(1));
            return BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        catch (Exception ex)
        {
            BacnetCollectorLog.DeviceInstanceParseFailed(logger, ex);
            return null;
        }
    }
}

internal static partial class BacnetCollectorLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "BACnet timeout reaching {Address}:{Port}."
    )]
    internal static partial void Timeout(ILogger logger, string address, int port);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "BACnet no fingerprints available for {Address}."
    )]
    internal static partial void NoFingerprints(ILogger logger, string address);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "BACnet socket error collecting {Address}: {SocketError}."
    )]
    internal static partial void SocketError(
        ILogger logger,
        string address,
        SocketError socketError,
        SocketException ex
    );

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "BACnet collection failed for {Address}."
    )]
    internal static partial void CollectionFailed(ILogger logger, string address, Exception ex);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "BACnet uint property parse failed."
    )]
    internal static partial void UIntParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Debug,
        Message = "BACnet string property parse failed."
    )]
    internal static partial void StringParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Debug,
        Message = "BACnet device instance parse failed."
    )]
    internal static partial void DeviceInstanceParseFailed(ILogger logger, Exception ex);
}