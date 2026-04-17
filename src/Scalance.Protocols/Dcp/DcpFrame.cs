using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Scalance.Protocols.Dcp;

/// <summary>
/// Pure (no-IO) builder and parser for PROFINET DCP Identify frames.
///
/// Frame format (big-endian throughout):
///   Ethernet II header
///     dst MAC      6 bytes   01:0E:CF:00:00:00 for Identify-All multicast
///     src MAC      6 bytes   local NIC MAC
///     ethertype    2 bytes   0x8892 (PROFINET)
///   PROFINET DCP header
///     FrameID      2 bytes   0xFEFE = Identify Request, 0xFEFF = Identify Response
///     ServiceID    1 byte    0x05 Identify
///     ServiceType  1 byte    0x00 Req / 0x01 Rsp
///     Xid          4 bytes   transaction id
///     ResponseDelay 2 bytes  (Req: 0x0001 = 1 unit; Rsp: 0x0000)
///     DCPDataLen   2 bytes   length of the following blocks
///     blocks       N bytes
///
/// Block format:
///     Option       1 byte
///     Suboption    1 byte
///     BlockLen     2 bytes   length of the block *value* field
///     Value        BlockLen bytes
///     Padding      0 or 1 byte so the next block starts on even offset
///
/// Identify-All request carries exactly one "All Selector" block
///     Option=0xFF Suboption=0xFF BlockLen=0x0000
///
/// Identify response carries DeviceProperties (0x02) and IP (0x01) blocks.
/// Response blocks include a 2-byte BlockInfo prefix in the value field.
/// </summary>
public static class DcpFrame
{
    public const int EtherType = 0x8892;
    public static readonly byte[] IdentifyAllMulticastMac = { 0x01, 0x0E, 0xCF, 0x00, 0x00, 0x00 };

    private const ushort FrameIdIdentifyRequest = 0xFEFE;
    private const ushort FrameIdIdentifyResponse = 0xFEFF;
    private const ushort FrameIdGetSet = 0xFEFD;

    private const byte ServiceIdIdentify = 0x05;
    private const byte ServiceIdSet = 0x04;
    private const byte ServiceTypeRequest = 0x00;
    private const byte ServiceTypeResponse = 0x01;

    private const byte OptionAll = 0xFF;
    private const byte SubOptionAll = 0xFF;

    private const byte OptionIp = 0x01;
    private const byte SubOptionIpParameter = 0x02;

    private const byte OptionDeviceProperties = 0x02;
    private const byte SubOptionVendorName = 0x01;
    private const byte SubOptionNameOfStation = 0x02;
    private const byte SubOptionDeviceId = 0x03;
    private const byte SubOptionDeviceRole = 0x04;

    private const byte OptionControl = 0x05;
    private const byte SubOptionSignal = 0x03;
    private const byte SubOptionResponse = 0x04;

    /// <summary>Block qualifier bit 0: 1 = save to non-volatile storage, 0 = temporary.</summary>
    private const ushort BlockQualifierSavePermanent = 0x0001;
    private const ushort BlockQualifierTemporary = 0x0000;
    /// <summary>Signal value 0x0100 = flash LED for approx 3 seconds.</summary>
    private const ushort SignalValueFlashOnce = 0x0100;

    /// <summary>
    /// Build an Identify-All request frame ready to be sent raw via pcap.
    /// </summary>
    public static byte[] BuildIdentifyAllRequest(byte[] srcMac, uint xid)
    {
        if (srcMac is null || srcMac.Length != 6) throw new ArgumentException("srcMac must be 6 bytes.", nameof(srcMac));

        // 14 (eth) + 10 (dcp hdr) + 4 (one all-selector block) = 28 bytes.
        // PROFINET requires 60-byte minimum Ethernet frames; many NICs pad
        // automatically but we pad here to be safe.
        const int minFrame = 60;
        var frame = new byte[minFrame];
        // Ethernet header
        Buffer.BlockCopy(IdentifyAllMulticastMac, 0, frame, 0, 6);
        Buffer.BlockCopy(srcMac,                  0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), EtherType);

        // DCP header
        var p = frame.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(p, FrameIdIdentifyRequest); // FrameID
        p[2] = ServiceIdIdentify;
        p[3] = ServiceTypeRequest;
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(8), 0x0001);        // ResponseDelay
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10), 0x0004);       // DCPDataLen = 4

        // Single block: Option=All, Suboption=All, BlockLen=0
        p[12] = OptionAll;
        p[13] = SubOptionAll;
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), 0);

        return frame;
    }

    /// <summary>
    /// Parse a received Ethernet frame as a DCP Identify response. Returns null
    /// when the frame is not a DCP Identify response for our XID or is malformed.
    /// </summary>
    public static DcpIdentifyResponse? TryParseIdentifyResponse(ReadOnlySpan<byte> frame, uint expectedXid)
    {
        if (frame.Length < 14 + 10) return null;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        if (etherType != EtherType) return null;

        var src = frame.Slice(6, 6).ToArray();
        var dcp = frame.Slice(14);
        var frameId = BinaryPrimitives.ReadUInt16BigEndian(dcp);
        if (frameId != FrameIdIdentifyResponse) return null;
        if (dcp[2] != ServiceIdIdentify || dcp[3] != ServiceTypeResponse) return null;

        var xid = BinaryPrimitives.ReadUInt32BigEndian(dcp.Slice(4));
        if (xid != expectedXid) return null;

        var dcpDataLen = BinaryPrimitives.ReadUInt16BigEndian(dcp.Slice(10));
        if (12 + dcpDataLen > dcp.Length) return null;

        var blocks = dcp.Slice(12, dcpDataLen);
        var rsp = new DcpIdentifyResponse { SourceMac = FormatMac(src) };

        int i = 0;
        while (i + 4 <= blocks.Length)
        {
            byte option = blocks[i];
            byte subOption = blocks[i + 1];
            int blockLen = BinaryPrimitives.ReadUInt16BigEndian(blocks.Slice(i + 2, 2));
            int valueStart = i + 4;
            if (valueStart + blockLen > blocks.Length) break;
            var value = blocks.Slice(valueStart, blockLen);

            // All response blocks carry BlockInfo (2 bytes) before the payload.
            var payload = value.Length >= 2 ? value.Slice(2) : ReadOnlySpan<byte>.Empty;

            switch ((option, subOption))
            {
                case (OptionDeviceProperties, SubOptionVendorName):
                    rsp.VendorName = DecodeAscii(payload);
                    break;
                case (OptionDeviceProperties, SubOptionNameOfStation):
                    rsp.NameOfStation = DecodeAscii(payload);
                    break;
                case (OptionDeviceProperties, SubOptionDeviceId) when payload.Length >= 4:
                    rsp.VendorId = BinaryPrimitives.ReadUInt16BigEndian(payload);
                    rsp.DeviceId = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2));
                    break;
                case (OptionDeviceProperties, SubOptionDeviceRole) when payload.Length >= 1:
                    rsp.DeviceRole = payload[0];
                    break;
                case (OptionIp, SubOptionIpParameter) when payload.Length >= 12:
                    rsp.IpAddress = new IPAddress(payload.Slice(0, 4).ToArray());
                    rsp.SubnetMask = new IPAddress(payload.Slice(4, 4).ToArray());
                    rsp.Gateway = new IPAddress(payload.Slice(8, 4).ToArray());
                    break;
            }

            i = valueStart + blockLen;
            if ((blockLen & 1) == 1) i++; // even-boundary padding
        }

        return rsp;
    }

    /// <summary>
    /// Build a DCP Set request that assigns IP / subnet mask / gateway to one
    /// target device (unicast). When <paramref name="savePermanent"/> is true,
    /// the device stores the setting in NVS (block qualifier bit 0 = 1);
    /// otherwise the value only survives until the next power cycle.
    /// </summary>
    public static byte[] BuildSetIpRequest(
        byte[] srcMac, byte[] dstMac, uint xid,
        IPAddress ip, IPAddress mask, IPAddress gateway, bool savePermanent)
    {
        if (srcMac is null || srcMac.Length != 6) throw new ArgumentException("srcMac must be 6 bytes.", nameof(srcMac));
        if (dstMac is null || dstMac.Length != 6) throw new ArgumentException("dstMac must be 6 bytes.", nameof(dstMac));

        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var gwBytes = gateway.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4 || gwBytes.Length != 4)
            throw new ArgumentException("IP, mask, and gateway must be IPv4 addresses.");

        // Block value = 2 (BlockQualifier) + 12 (IP+mask+gw) = 14. No pad (even).
        // Total = 14 (eth) + 10 (dcp hdr) + 4 (block hdr) + 14 (block value) = 42
        const int minFrame = 60;
        var frame = new byte[minFrame];

        Buffer.BlockCopy(dstMac, 0, frame, 0, 6);
        Buffer.BlockCopy(srcMac, 0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), EtherType);

        var p = frame.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(p, FrameIdGetSet);
        p[2] = ServiceIdSet;
        p[3] = ServiceTypeRequest;
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(8), 0x0000);          // ResponseDelay unused for Set
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10), 18);             // DCPDataLen = 4 + 14

        // Block: Option=IP(0x01), Suboption=IpParameter(0x02), BlockLen=14
        p[12] = OptionIp;
        p[13] = SubOptionIpParameter;
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), 14);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(16),
            savePermanent ? BlockQualifierSavePermanent : BlockQualifierTemporary);
        Buffer.BlockCopy(ipBytes,   0, frame, 14 + 18, 4);
        Buffer.BlockCopy(maskBytes, 0, frame, 14 + 22, 4);
        Buffer.BlockCopy(gwBytes,   0, frame, 14 + 26, 4);

        return frame;
    }

    /// <summary>
    /// Build a DCP Set request that triggers the "flash LED" signal on one
    /// target device. Useful for physically identifying which device on the
    /// rack corresponds to a MAC address.
    /// </summary>
    public static byte[] BuildFlashLedRequest(byte[] srcMac, byte[] dstMac, uint xid)
    {
        if (srcMac is null || srcMac.Length != 6) throw new ArgumentException("srcMac must be 6 bytes.", nameof(srcMac));
        if (dstMac is null || dstMac.Length != 6) throw new ArgumentException("dstMac must be 6 bytes.", nameof(dstMac));

        const int minFrame = 60;
        var frame = new byte[minFrame];

        Buffer.BlockCopy(dstMac, 0, frame, 0, 6);
        Buffer.BlockCopy(srcMac, 0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), EtherType);

        var p = frame.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(p, FrameIdGetSet);
        p[2] = ServiceIdSet;
        p[3] = ServiceTypeRequest;
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(8), 0x0000);          // ResponseDelay
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(10), 8);              // DCPDataLen = 4 + 4

        // Block: Option=Control(0x05), Suboption=Signal(0x03), BlockLen=4
        p[12] = OptionControl;
        p[13] = SubOptionSignal;
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), 4);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(16), BlockQualifierTemporary);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(18), SignalValueFlashOnce);

        return frame;
    }

    /// <summary>
    /// Parse a received Ethernet frame as a DCP Set response. Returns null
    /// when not a DCP Set response for our XID or malformed. The response
    /// carries a Control/Response block whose last byte is a BlockError
    /// code (0 = OK, non-zero = one of the DCP error codes).
    /// </summary>
    public static DcpSetResponse? TryParseSetResponse(ReadOnlySpan<byte> frame, uint expectedXid)
    {
        if (frame.Length < 14 + 10) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2)) != EtherType) return null;

        var src = frame.Slice(6, 6).ToArray();
        var dcp = frame.Slice(14);
        if (BinaryPrimitives.ReadUInt16BigEndian(dcp) != FrameIdGetSet) return null;
        if (dcp[2] != ServiceIdSet || dcp[3] != ServiceTypeResponse) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(dcp.Slice(4)) != expectedXid) return null;

        var dcpDataLen = BinaryPrimitives.ReadUInt16BigEndian(dcp.Slice(10));
        if (12 + dcpDataLen > dcp.Length) return null;

        // Default = success with no explicit block. Some devices reply with only
        // ServiceType=Response and an empty data section for trivial ops.
        var rsp = new DcpSetResponse
        {
            SourceMac = FormatMac(src),
            Success = true,
            BlockError = 0,
            ResponseOption = 0,
            ResponseSubOption = 0,
        };

        var blocks = dcp.Slice(12, dcpDataLen);
        int i = 0;
        while (i + 4 <= blocks.Length)
        {
            byte option = blocks[i];
            byte subOption = blocks[i + 1];
            int blockLen = BinaryPrimitives.ReadUInt16BigEndian(blocks.Slice(i + 2, 2));
            int valueStart = i + 4;
            if (valueStart + blockLen > blocks.Length) break;
            var value = blocks.Slice(valueStart, blockLen);

            if (option == OptionControl && subOption == SubOptionResponse && value.Length >= 3)
            {
                rsp.ResponseOption = value[0];
                rsp.ResponseSubOption = value[1];
                rsp.BlockError = value[2];
                rsp.Success = rsp.BlockError == 0;
                rsp.ErrorMessage = rsp.Success ? null : DescribeBlockError(rsp.BlockError);
            }

            i = valueStart + blockLen;
            if ((blockLen & 1) == 1) i++;
        }

        return rsp;
    }

    private static string DescribeBlockError(byte code) => code switch
    {
        0x00 => "OK",
        0x01 => "Option unsupported",
        0x02 => "Suboption unsupported or no DataSet available",
        0x03 => "Suboption not set",
        0x04 => "Resource error",
        0x05 => "SetNotPossible due to local constraints",
        0x06 => "In operation",
        _    => $"Unknown error 0x{code:X2}",
    };

    private static string DecodeAscii(ReadOnlySpan<byte> data)
    {
        // Strings are null-terminated in practice; trim trailing NULs and spaces.
        int end = data.Length;
        while (end > 0 && (data[end - 1] == 0 || data[end - 1] == ' ')) end--;
        return Encoding.ASCII.GetString(data.Slice(0, end));
    }

    private static string FormatMac(byte[] mac) =>
        string.Join(":", mac.Select(b => b.ToString("X2")));
}

public sealed class DcpSetResponse
{
    public string SourceMac { get; set; } = "";
    public bool Success { get; set; }
    public byte BlockError { get; set; }
    public byte ResponseOption { get; set; }
    public byte ResponseSubOption { get; set; }
    public string? ErrorMessage { get; set; }

    public override string ToString() => Success
        ? $"OK ({SourceMac})"
        : $"FAIL 0x{BlockError:X2} {ErrorMessage} ({SourceMac})";
}

public sealed class DcpIdentifyResponse
{
    public string SourceMac { get; set; } = "";
    public string? VendorName { get; set; }
    public string? NameOfStation { get; set; }
    public int? VendorId { get; set; }
    public int? DeviceId { get; set; }
    public byte? DeviceRole { get; set; }
    public IPAddress? IpAddress { get; set; }
    public IPAddress? SubnetMask { get; set; }
    public IPAddress? Gateway { get; set; }

    public override string ToString() =>
        $"{NameOfStation ?? "?"} [{SourceMac}] {IpAddress} vendor={VendorName} id={VendorId:X4}:{DeviceId:X4}";
}
