using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Pure-managed BACnet/IP wire protocol helpers — read-only operations only.
/// Covers BVLL/NPDU/APDU framing, Who-Is/I-Am parsing, and ReadProperty encoding.
/// No external dependencies; all networking via System.Net.Sockets.
/// </summary>
internal static class BacnetClient
{
    // ReadProperty request prefix targeting Device object instance 4194303 (wildcard).
    private static ReadOnlySpan<byte> ReadPropertyPrefix =>
    [
        0x81, // BVLL Type: BACnet/IP v4
        0x0a, // BVLL Function: Original-Unicast-NPDU
        0x00, 0x11, // BVLL Length: 17 bytes total
        0x01, // NPDU Version: 1 (ASHRAE 135-1995)
        0x04, // NPDU Control: expecting reply
        0x00, // APDU Type: Confirmed-REQ, PDU flags: 0
        0x05, // Max segments unspecified; max APDU size: 1476 bytes
        0x01, // Invoke ID: 1
        0x0c, // Service Choice: ReadProperty
        0x0c, // Context tag 0, length 4
        0x02, 0x3f, 0xff, 0xff, // Object: DEVICE, instance 4194303
        0x19, // Context tag 1, length 1 (property identifier follows)
    ];

    // Who-Is broadcast packet — local subnet broadcast, no destination specifier.
    private static ReadOnlySpan<byte> WhoIsPacket =>
    [
        0x81, // BVLL Type: BACnet/IP v4
        0x0b, // BVLL Function: Original-Broadcast-NPDU
        0x00, 0x08, // BVLL Length: 8 bytes
        0x01, // NPDU Version: 1
        0x00, // NPDU Control: no flags (local broadcast only)
        0x10, // APDU Type: Unconfirmed-REQ
        0x08, // Service Choice: Who-Is
    ];

    public static byte[] BuildWhoIsRequest() => WhoIsPacket.ToArray();

    /// <summary>
    /// Builds a ReadProperty request for the given BACnet property, targeting
    /// the Device object with wildcard instance 4194303.
    /// </summary>
    public static byte[] BuildReadPropertyRequest(BacnetPropertyId property)
    {
        // Encode property code as big-endian, trimming leading zero bytes.
        Span<byte> propBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(propBytes, (int)property);
        propBytes = propBytes.TrimStart((byte)0x00);

        byte[] request = new byte[ReadPropertyPrefix.Length + propBytes.Length];
        ReadPropertyPrefix.CopyTo(request);
        propBytes.CopyTo(request.AsSpan(ReadPropertyPrefix.Length));
        return request;
    }

    /// <summary>
    /// Sends a UDP packet and receives the response, with per-request timeout.
    /// Returns null on timeout or cancellation (not the caller's ct).
    /// </summary>
    public static async ValueTask<byte[]?> SendReceiveAsync(
        UdpClient udp,
        byte[] request,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        try
        {
            await udp.SendAsync(request.AsMemory(), linked.Token);
            UdpReceiveResult result = await udp.ReceiveAsync(linked.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout — not caller's cancellation
        }
    }

    /// <summary>
    /// Strips the BVLL header (4 bytes) and NPDU (variable) to return the raw APDU span.
    /// Throws InvalidOperationException if the packet is malformed or uses an unsupported version.
    /// </summary>
    public static ReadOnlySpan<byte> StripBvllAndNpdu(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 6)
        {
            throw new InvalidOperationException("Packet too short.");
        }

        buffer = buffer.Slice(4); // Remove BVLL header (type, function, length)

        if (buffer[0] != 1)
        {
            throw new InvalidOperationException("Unsupported NPDU version.");
        }

        buffer = buffer.Slice(1); // Remove NPDU version

        bool hasDestination = (buffer[0] & 0b_0010_0000) != 0;
        bool hasSource = (buffer[0] & 0b_0000_1000) != 0;
        bool hasMessageType = (buffer[0] & 0b_1000_0000) != 0;
        buffer = buffer.Slice(1); // Remove NPDU control octet

        if (hasDestination)
        {
            if (buffer.Length < 3) // DNET (2) + DLEN (1) minimum
            {
                throw new InvalidOperationException("Packet too short for NPDU destination address.");
            }

            buffer = buffer.Slice(2); // DNET (2 bytes)
            byte dlen = buffer[0];
            if (buffer.Length < dlen + 2) // DLEN + DADR bytes + hop count (1)
            {
                throw new InvalidOperationException("Packet too short for NPDU destination address data.");
            }

            buffer = buffer.Slice(dlen + 1); // DLEN + DADR
            buffer = buffer.Slice(1); // Hop count
        }

        if (hasSource)
        {
            if (buffer.Length < 3) // SNET (2) + SLEN (1) minimum
            {
                throw new InvalidOperationException("Packet too short for NPDU source address.");
            }

            buffer = buffer.Slice(2); // SNET (2 bytes)
            byte slen = buffer[0];
            if (buffer.Length < slen + 1) // SLEN + SADR bytes
            {
                throw new InvalidOperationException("Packet too short for NPDU source address data.");
            }

            buffer = buffer.Slice(slen + 1); // SLEN + SADR
        }

        if (hasMessageType)
        {
            if (buffer.Length < 1)
            {
                throw new InvalidOperationException("Packet too short for NPDU message type.");
            }

            if (buffer[0] is >= 0x80 and <= 0xFF)
            {
                if (buffer.Length < 2)
                {
                    throw new InvalidOperationException("Packet too short for NPDU vendor ID.");
                }

                buffer = buffer.Slice(1); // Vendor ID (only present for vendor-proprietary messages)
            }

            buffer = buffer.Slice(1); // Message type
        }

        if (buffer.IsEmpty)
        {
            throw new InvalidOperationException("APDU is empty after NPDU strip.");
        }

        return buffer;
    }

    /// <summary>
    /// Parses an I-Am APDU to extract the device instance number and vendor ID.
    /// Returns null if the packet is not a valid I-Am for a Device object.
    /// </summary>
    public static (uint DeviceInstance, ushort VendorId)? ParseIAm(ReadOnlySpan<byte> apdu)
    {
        // Minimum I-Am: PDU type (1) + service (1) + object-id tag (1) + object-id (4) = 7 bytes
        if (apdu.Length < 7)
        {
            return null;
        }

        if ((apdu[0] & 0xF0) != 0x10)
        {
            return null; // Must be Unconfirmed-REQ
        }

        if (apdu[1] != 0x00)
        {
            return null; // Service: I-Am
        }

        if (apdu[2] != 0xC4)
        {
            return null; // Application tag 12 (object-identifier), length 4
        }

        // Object-identifier: upper 10 bits = object type, lower 22 bits = instance.
        uint objectId = BinaryPrimitives.ReadUInt32BigEndian(apdu.Slice(3, 4));
        uint objectType = objectId >> 22;
        if (objectType != 8)
        {
            return null; // 8 = DEVICE
        }

        uint instance = objectId & 0x003FFFFF;

        // Extract vendor ID by walking past max-APDU-size and segmentation TLVs.
        // For the well-known I-Am fields these are always short (LVT < 5 = no extended length).
        ushort vendorId = 0;
        int idx = 7;
        try
        {
            // Skip max APDU size TLV (tag + N bytes)
            if (idx < apdu.Length)
            {
                int lvt = apdu[idx] & 0x07;
                idx += 1 + (lvt < 5 ? lvt : 1); // LVT<5: length is inline; =5: 1-byte extended follows
            }

            // Skip segmentation TLV
            if (idx < apdu.Length)
            {
                int lvt = apdu[idx] & 0x07;
                idx += 1 + (lvt < 5 ? lvt : 1);
            }

            // Read vendor ID TLV
            if (idx < apdu.Length)
            {
                int lvt = apdu[idx] & 0x07;
                idx++;
                vendorId = lvt switch
                {
                    1 when idx < apdu.Length => apdu[idx],
                    2 when idx + 1 < apdu.Length => BinaryPrimitives.ReadUInt16BigEndian(apdu.Slice(idx, 2)),
                    _ => 0,
                };
            }
        }
        catch
        {
            /* vendor ID is best-effort */
        }

        return (instance, vendorId);
    }

    /// <summary>
    /// Reads a BACnet TLV tag-length from the front of buffer and advances past it.
    /// Returns the length value (not the tag byte itself).
    /// </summary>
    public static int GetTagLength(ref ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            throw new InvalidOperationException("Buffer is empty.");
        }

        int tag = buffer[0];
        buffer = buffer.Slice(1);
        int lvt = tag & 0b_0000_0111;

        if (lvt < 5)
        {
            return lvt;
        }

        if (lvt > 5)
        {
            throw new InvalidOperationException("Invalid length value type.");
        }

        // LVT = 5: extended length in next byte(s)
        if (buffer.IsEmpty)
        {
            throw new InvalidOperationException("Buffer too short for extended tag length.");
        }

        byte first = buffer[0];
        buffer = buffer.Slice(1);
        if (first < 254)
        {
            return first;
        }
        else if (first == 254)
        {
            if (buffer.Length < 2)
            {
                throw new InvalidOperationException("Buffer too short for 16-bit extended tag length.");
            }

            int v = BinaryPrimitives.ReadInt16BigEndian(buffer.Slice(0, 2));
            buffer = buffer.Slice(2);
            return v;
        }
        else
        {
            if (buffer.Length < 4)
            {
                throw new InvalidOperationException("Buffer too short for 32-bit extended tag length.");
            }

            int v = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(0, 4));
            buffer = buffer.Slice(4);
            return v;
        }
    }

    /// <summary>
    /// Extracts an unsigned integer value from a ComplexACK ReadProperty APDU.
    /// Used for numeric properties like VendorIdentifier.
    /// Returns null on Error, Abort, or if the value is not an UNSIGNED INTEGER application tag.
    /// </summary>
    public static uint? ExtractUnsignedValue(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        int pduType = buffer[0] & 0xF0;
        if (pduType is 0x50 or 0x60 or 0x70)
        {
            return null;
        }

        buffer = buffer.Slice(1); // APDU type
        buffer = buffer.Slice(1); // Invoke ID
        buffer = buffer.Slice(1); // Service choice (ReadProperty = 0x0C)
        buffer = buffer.Slice(5); // Object identifier (tag + 4-byte value)

        int propLen = GetTagLength(ref buffer);
        if (propLen < 0 || propLen > buffer.Length)
        {
            return null;
        }

        buffer = buffer.Slice(propLen); // Property identifier value

        if (buffer.IsEmpty)
        {
            return null;
        }

        buffer = buffer.Slice(1); // Opening wrapper byte 0x3E

        // BACnet application tag 2 = UNSIGNED INTEGER
        if (buffer.IsEmpty)
        {
            return null;
        }

        int tag = buffer[0];
        int appTag = (tag >> 4) & 0x0F;
        if (appTag != 2)
        {
            return null; // not UNSIGNED INTEGER
        }

        int lvt = tag & 0x07;
        buffer = buffer.Slice(1);

        return lvt switch
        {
            1 when buffer.Length >= 1 => buffer[0],
            2 when buffer.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]),
            4 when buffer.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]),
            _ => null,
        };
    }

    /// <summary>
    /// Extracts the string value from a ComplexACK ReadProperty APDU.
    /// Returns null on Error or Abort; returns empty string on Reject (property not supported).
    /// </summary>
    public static string? ExtractStringValue(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        int pduType = buffer[0] & 0xF0;
        if (pduType == 0x50)
        {
            return null; // Error PDU
        }

        if (pduType == 0x60)
        {
            return string.Empty; // Reject PDU (property not supported by device)
        }

        if (pduType == 0x70)
        {
            return null; // Abort PDU
        }

        buffer = buffer.Slice(1); // APDU type
        buffer = buffer.Slice(1); // Invoke ID
        buffer = buffer.Slice(1); // Service choice (ReadProperty = 0x0C)
        buffer = buffer.Slice(5); // Object identifier (tag + 4-byte value)

        int propLen = GetTagLength(ref buffer);
        if (propLen < 0 || propLen > buffer.Length)
        {
            return null;
        }

        buffer = buffer.Slice(propLen); // Property identifier value

        if (buffer.IsEmpty)
        {
            return null;
        }

        buffer = buffer.Slice(1); // Opening wrapper byte 0x3E

        int valueLen = GetTagLength(ref buffer);
        if (valueLen <= 0 || valueLen > 4096 || buffer.IsEmpty)
        {
            return null;
        }

        Encoding enc = buffer[0] switch
        {
            0 => Encoding.UTF8,
            3 => Encoding.GetEncoding("utf-32BE"),
            4 => Encoding.GetEncoding("utf-16BE"),
            5 => Encoding.Latin1,
            _ => Encoding.ASCII,
        };
        buffer = buffer.Slice(1); // Remove charset byte
        buffer = buffer.Slice(0, valueLen - 1); // Remaining bytes are the string

        return enc.GetString(buffer);
    }
}

/// <summary>
/// BACnet property identifier codes for Device object properties.
/// Values are decimal property identifiers per ASHRAE 135.
/// </summary>
internal enum BacnetPropertyId
{
    ObjectIdentifier = 0x4B, // 75
    ObjectName = 0x4D, // 77
    Description = 0x1C, // 28
    VendorName = 0x79, // 121
    VendorIdentifier = 0x78, // 120
    ModelName = 0x46, // 70
    FirmwareRevision = 0x2C, // 44
    ApplicationSoftwareVersion = 0x0C, // 12
    Location = 0x3A, // 58
    SystemStatus = 0x70, // 112
    SerialNumber = 0x0174, // 372
}