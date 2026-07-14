using System.Buffers.Binary;
using System.Net.Sockets;

using JMW.Discovery.Agent.Collection.Device;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers Modbus TCP devices by probing port 502 on ARP-known neighbors.
/// Sends a minimal Read Holding Registers request (FC 03, 1 register) and
/// considers any valid response — including exception responses — as confirmation
/// that a Modbus device is present. Source tag: "modbus".
/// </summary>
public sealed class ModbusScanner : UnicastScannerBase
{
    private const int ModbusPort = 502;
    private const int TimeoutMs = 1000;

    public override string Name => "modbus";

    protected override int MaxConcurrency => 20;

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcp = await SocketProbe.TryConnectAsync(ip, ModbusPort, TimeoutMs, ct);
            if (tcp is null)
            {
                return null;
            }

            tcp.ReceiveTimeout = TimeoutMs;
            tcp.SendTimeout = TimeoutMs;

            using CancellationTokenSource timeoutCts = new(TimeoutMs);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

            NetworkStream stream = tcp.GetStream();
            stream.ReadTimeout = TimeoutMs;
            stream.WriteTimeout = TimeoutMs;

            // FC 03: Read 1 holding register at address 0, unit ID 1
            byte[] request = ModbusClient.BuildReadHoldingRegisters(
                transactionId: 1,
                unitId: 1,
                startAddress: 0,
                count: 1
            );
            await stream.WriteAsync(request.AsMemory(), linked.Token);

            byte[] buffer = new byte[256];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token);
            if (read < 7)
            {
                return null;
            }

            byte[] response = buffer[..read];

            // Accept valid response OR exception response (FC | 0x80) — both confirm Modbus.
            // A non-Modbus TCP service would not respond with a valid MBAP header.
            if (!IsModbusResponse(response))
            {
                return null;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Source = "modbus",
                Attributes =
                {
                    ["modbus.unit_id"] = "1",
                    ["modbus.port"] = ModbusPort.ToString(),
                },
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout
        }
        catch
        {
            return null;
        }
    }

    private static bool IsModbusResponse(byte[] response)
    {
        if (response.Length < 7)
        {
            return false;
        }

        // MBAP Protocol ID must be 0x0000 for Modbus
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        return protocolId == 0;
    }
}