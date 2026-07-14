using System.Buffers.Binary;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Pure-managed Modbus TCP wire protocol helpers — read-only operations only.
/// Covers MBAP header framing and read function codes (FC 01, 03, 04).
/// No external dependencies; all networking via System.Net.Sockets.
/// </summary>
internal static class ModbusClient
{
    // Modbus TCP MBAP header is 6 bytes + 1-byte unit ID = 7 bytes before the PDU.
    private const int MbapLength = 7;

    /// <summary>
    /// Builds a Read Holding Registers (FC 03) request.
    /// startAddress and count are zero-based register indices.
    /// </summary>
    public static byte[] BuildReadHoldingRegisters(
        ushort transactionId,
        byte unitId,
        ushort startAddress,
        ushort count
    )
        => BuildReadRequest(transactionId, unitId, 0x03, startAddress, count);

    /// <summary>
    /// Builds a Read Input Registers (FC 04) request.
    /// </summary>
    public static byte[] BuildReadInputRegisters(
        ushort transactionId,
        byte unitId,
        ushort startAddress,
        ushort count
    )
        => BuildReadRequest(transactionId, unitId, 0x04, startAddress, count);

    /// <summary>
    /// Builds a Read Coils (FC 01) request.
    /// </summary>
    public static byte[] BuildReadCoils(
        ushort transactionId,
        byte unitId,
        ushort startAddress,
        ushort count
    )
        => BuildReadRequest(transactionId, unitId, 0x01, startAddress, count);

    /// <summary>
    /// Builds a FC 43 / MEI Type 14 Read Device Identification request (basic objects).
    /// Requests object IDs 0x00 (VendorName), 0x01 (ProductCode), 0x02 (MajorMinorRevision).
    /// </summary>
    public static byte[] BuildReadDeviceIdentification(ushort transactionId, byte unitId)
    {
        // MBAP (6 bytes) + Unit ID (1) + FC (1) + MEI Type (1) + ReadDevIdCode (1) + ObjectId (1) = 11 bytes
        // Length field = 5 (unit ID + 4-byte PDU)
        byte[] packet = new byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0); // Protocol ID = 0
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 5); // Length = 5
        packet[6] = unitId;
        packet[7] = 0x2B; // FC 43: Encapsulated Interface Transport
        packet[8] = 0x0E; // MEI Type 14: Read Device Identification
        packet[9] = 0x01; // ReadDevIdCode = 0x01 (basic: VendorName, ProductCode, Revision)
        packet[10] = 0x00; // Start from object 0x00
        return packet;
    }

    /// <summary>
    /// Parses a FC 43 / MEI Type 14 response into a dictionary of object ID → UTF-8 string.
    /// Returns null if the response is malformed or not an FC 43 MEI Type 14 response.
    /// Object IDs: 0x00=VendorName, 0x01=ProductCode, 0x02=MajorMinorRevision.
    /// </summary>
    public static Dictionary<byte, string>? ParseDeviceIdentification(
        byte[] response,
        ushort expectedTransactionId
    )
    {
        // Minimum: MBAP (7) + FC (1) + MEI (1) + ReadDevIdCode (1) + Conformity (1) + More (1) + NextId (1) + NumObjects (1) = 14
        if (response.Length < 14)
        {
            return null;
        }

        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0));
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));

        if (transactionId != expectedTransactionId || protocolId != 0)
        {
            return null;
        }

        byte fc = response[MbapLength]; // byte 7
        if (fc != 0x2B)
        {
            return null; // not FC 43, or exception response (0xAB)
        }

        int offset = MbapLength + 1; // start at MEI Type byte
        if (response[offset] != 0x0E)
        {
            return null; // not MEI Type 14
        }

        offset++; // MEI Type (0x0E)
        offset++; // ReadDevIdCode echo
        offset++; // Conformity Level
        offset++; // More Follows (0x00 = no more, 0xFF = more available)
        offset++; // Next Object Id (0x00 if no more)
        int numObjects = response[offset++];

        if (numObjects == 0)
        {
            return null;
        }

        Dictionary<byte, string> result = new(numObjects);
        for (int i = 0; i < numObjects; i++)
        {
            if (offset + 2 > response.Length)
            {
                break;
            }

            byte objId = response[offset++];
            int objLen = response[offset++];
            if (offset + objLen > response.Length)
            {
                break;
            }

            string value = Encoding.UTF8.GetString(response, offset, objLen);
            offset += objLen;
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[objId] = value.Trim();
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static byte[] BuildReadRequest(
        ushort transactionId,
        byte unitId,
        byte functionCode,
        ushort startAddress,
        ushort count
    )
    {
        // MBAP header (6 bytes) + Unit ID (1) + FC (1) + Start (2) + Count (2) = 12 bytes
        byte[] packet = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), transactionId); // Transaction ID
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0); // Protocol ID = 0 (Modbus)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 6); // Length = 6 (remaining bytes)
        packet[6] = unitId;
        packet[7] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), count);
        return packet;
    }

    /// <summary>
    /// Parses a Modbus TCP response. Returns (functionCode, data) where data is the
    /// PDU payload bytes (after the function code). Returns null if:
    /// - Response is too short or malformed
    /// - Protocol ID is not 0 (not Modbus)
    /// - Function code indicates an exception response (FC | 0x80)
    /// </summary>
    public static (byte FunctionCode, byte[] Data)? ParseResponse(
        byte[] response,
        ushort expectedTransactionId
    )
    {
        if (response.Length < MbapLength + 1)
        {
            return null;
        }

        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0));
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4));

        if (transactionId != expectedTransactionId)
        {
            return null;
        }

        if (protocolId != 0)
        {
            return null; // not Modbus TCP
        }

        if (response.Length < MbapLength + length - 1)
        {
            return null;
        }

        byte functionCode = response[MbapLength]; // byte 7 (0-indexed)

        // Exception response: function code has high bit set (e.g. FC 03 → 0x83 on error)
        if ((functionCode & 0x80) != 0)
        {
            return null;
        }

        byte[] data = response[(MbapLength + 1)..];
        return (functionCode, data);
    }

    /// <summary>
    /// Decodes register response data (byte count + big-endian uint16 pairs) into register values.
    /// </summary>
    public static ushort[] DecodeRegisters(byte[] data)
    {
        if (data.Length < 1)
        {
            return [];
        }

        int byteCount = data[0];
        int registerCount = byteCount / 2;
        ushort[] registers = new ushort[registerCount];
        for (int i = 0; i < registerCount; i++)
        {
            int offset = 1 + (i * 2);
            if (offset + 1 >= data.Length)
            {
                break;
            }

            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        }

        return registers;
    }

    /// <summary>
    /// Decodes coil response data (byte count + packed bits) into bool values.
    /// </summary>
    public static bool[] DecodeCoils(byte[] data, int requestedCount)
    {
        if (data.Length < 1)
        {
            return [];
        }

        bool[] coils = new bool[requestedCount];
        for (int i = 0; i < requestedCount; i++)
        {
            int byteIdx = 1 + (i / 8);
            if (byteIdx >= data.Length)
            {
                break;
            }

            coils[i] = (data[byteIdx] & (1 << (i % 8))) != 0;
        }

        return coils;
    }
}