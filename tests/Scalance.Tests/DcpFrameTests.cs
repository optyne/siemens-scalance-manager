using System.Buffers.Binary;
using System.Net;
using FluentAssertions;
using Scalance.Protocols.Dcp;

namespace Scalance.Tests;

public class DcpFrameTests
{
    private static readonly byte[] SampleSrcMac = { 0x00, 0x1B, 0x1B, 0xAA, 0xBB, 0xCC };
    private static readonly byte[] SampleDstMac = { 0xD4, 0xF5, 0x27, 0x68, 0xA7, 0x2C };

    [Fact]
    public void BuildIdentifyAllRequest_has_correct_ethernet_and_dcp_header()
    {
        var req = DcpFrame.BuildIdentifyAllRequest(SampleSrcMac, 0x12345678);

        req.Length.Should().BeGreaterThanOrEqualTo(60, "PROFINET frames must be at least 60 bytes");
        // dst MAC = multicast 01:0E:CF:00:00:00
        req.AsSpan(0, 6).ToArray().Should().Equal(new byte[] { 0x01, 0x0E, 0xCF, 0x00, 0x00, 0x00 });
        req.AsSpan(6, 6).ToArray().Should().Equal(SampleSrcMac);
        // ethertype = 0x8892
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(12, 2)).Should().Be(0x8892);
        // FrameID = 0xFEFE (Identify Request)
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(14, 2)).Should().Be(0xFEFE);
        // ServiceID = Identify (0x05), ServiceType = Request (0x00)
        req[16].Should().Be(0x05);
        req[17].Should().Be(0x00);
        // Xid preserved
        BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(18, 4)).Should().Be(0x12345678u);
        // DCPDataLen = 4
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(24, 2)).Should().Be((ushort)4);
        // All-Selector block: 0xFF 0xFF 0x00 0x00
        req.AsSpan(26, 4).ToArray().Should().Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });
    }

    [Fact]
    public void BuildIdentifyAllRequest_rejects_invalid_mac()
    {
        Action act = () => DcpFrame.BuildIdentifyAllRequest(new byte[] { 1, 2, 3 }, 0);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParseIdentifyResponse_extracts_name_ip_vendor_and_device_id()
    {
        // Craft a synthetic response frame
        var rsp = BuildSyntheticResponse(
            srcMac: new byte[] { 0x08, 0x00, 0x06, 0x99, 0x88, 0x77 },
            xid: 0xDEADBEEF,
            nameOfStation: "scalance-test",
            vendorName: "Siemens",
            vendorId: 0x002A, deviceId: 0x0301, deviceRole: 0x02,
            ip: new byte[] { 192, 168, 1, 10 },
            mask: new byte[] { 255, 255, 255, 0 },
            gw: new byte[] { 192, 168, 1, 1 });

        var parsed = DcpFrame.TryParseIdentifyResponse(rsp, 0xDEADBEEF);

        parsed.Should().NotBeNull();
        parsed!.SourceMac.Should().Be("08:00:06:99:88:77");
        parsed.NameOfStation.Should().Be("scalance-test");
        parsed.VendorName.Should().Be("Siemens");
        parsed.VendorId.Should().Be(0x002A);
        parsed.DeviceId.Should().Be(0x0301);
        parsed.DeviceRole.Should().Be((byte)0x02);
        parsed.IpAddress!.ToString().Should().Be("192.168.1.10");
        parsed.SubnetMask!.ToString().Should().Be("255.255.255.0");
        parsed.Gateway!.ToString().Should().Be("192.168.1.1");
    }

    [Fact]
    public void TryParseIdentifyResponse_returns_null_when_xid_does_not_match()
    {
        var rsp = BuildSyntheticResponse(
            srcMac: new byte[] { 0, 0, 0, 0, 0, 1 },
            xid: 1,
            nameOfStation: "foo",
            vendorName: "bar",
            vendorId: 1, deviceId: 1, deviceRole: 0,
            ip: new byte[] { 0, 0, 0, 0 },
            mask: new byte[] { 0, 0, 0, 0 },
            gw: new byte[] { 0, 0, 0, 0 });
        DcpFrame.TryParseIdentifyResponse(rsp, 2).Should().BeNull();
    }

    [Fact]
    public void TryParseIdentifyResponse_returns_null_on_wrong_ethertype()
    {
        var frame = new byte[60];
        // ethertype 0x0800 (IPv4) — should be rejected
        frame[12] = 0x08; frame[13] = 0x00;
        DcpFrame.TryParseIdentifyResponse(frame, 0).Should().BeNull();
    }

    [Fact]
    public void BuildSetIpRequest_encodes_ip_mask_gateway_and_save_qualifier()
    {
        var req = DcpFrame.BuildSetIpRequest(
            SampleSrcMac, SampleDstMac, 0xAABBCCDD,
            IPAddress.Parse("10.170.33.200"),
            IPAddress.Parse("255.255.255.0"),
            IPAddress.Parse("10.170.33.1"),
            savePermanent: true);

        req.Length.Should().BeGreaterThanOrEqualTo(60);
        req.AsSpan(0, 6).ToArray().Should().Equal(SampleDstMac);
        req.AsSpan(6, 6).ToArray().Should().Equal(SampleSrcMac);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(12, 2)).Should().Be(0x8892);
        // FrameID = 0xFEFD (Get/Set)
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(14, 2)).Should().Be(0xFEFD);
        // ServiceID = Set (0x04), ServiceType = Request (0x00)
        req[16].Should().Be(0x04);
        req[17].Should().Be(0x00);
        BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(18, 4)).Should().Be(0xAABBCCDDu);
        // DCPDataLen = 4 (block hdr) + 14 (block value) = 18
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(24, 2)).Should().Be((ushort)18);
        // Block header: Option=0x01 (IP), Suboption=0x02 (IP parameter), BlockLen=14
        req[26].Should().Be(0x01);
        req[27].Should().Be(0x02);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(28, 2)).Should().Be((ushort)14);
        // BlockQualifier = 0x0001 (save permanent)
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(30, 2)).Should().Be((ushort)0x0001);
        // IP / mask / gateway
        req.AsSpan(32, 4).ToArray().Should().Equal(new byte[] { 10, 170, 33, 200 });
        req.AsSpan(36, 4).ToArray().Should().Equal(new byte[] { 255, 255, 255, 0 });
        req.AsSpan(40, 4).ToArray().Should().Equal(new byte[] { 10, 170, 33, 1 });
    }

    [Fact]
    public void BuildSetIpRequest_temporary_qualifier_is_zero()
    {
        var req = DcpFrame.BuildSetIpRequest(
            SampleSrcMac, SampleDstMac, 1,
            IPAddress.Parse("1.2.3.4"),
            IPAddress.Parse("255.0.0.0"),
            IPAddress.Parse("1.2.3.1"),
            savePermanent: false);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(30, 2)).Should().Be((ushort)0);
    }

    [Fact]
    public void BuildSetIpRequest_rejects_invalid_macs()
    {
        Action badSrc = () => DcpFrame.BuildSetIpRequest(
            new byte[3], SampleDstMac, 0,
            IPAddress.Any, IPAddress.Any, IPAddress.Any, false);
        badSrc.Should().Throw<ArgumentException>();

        Action badDst = () => DcpFrame.BuildSetIpRequest(
            SampleSrcMac, new byte[3], 0,
            IPAddress.Any, IPAddress.Any, IPAddress.Any, false);
        badDst.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildFlashLedRequest_encodes_control_signal_block()
    {
        var req = DcpFrame.BuildFlashLedRequest(SampleSrcMac, SampleDstMac, 0x11223344);

        req.Length.Should().BeGreaterThanOrEqualTo(60);
        req.AsSpan(0, 6).ToArray().Should().Equal(SampleDstMac);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(14, 2)).Should().Be(0xFEFD);
        req[16].Should().Be(0x04); // Set
        req[17].Should().Be(0x00);
        BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(18, 4)).Should().Be(0x11223344u);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(24, 2)).Should().Be((ushort)8);
        req[26].Should().Be(0x05); // Control
        req[27].Should().Be(0x03); // Signal
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(28, 2)).Should().Be((ushort)4);
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(30, 2)).Should().Be((ushort)0); // BlockQualifier
        BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(32, 2)).Should().Be((ushort)0x0100); // flash once
    }

    [Fact]
    public void TryParseSetResponse_success_when_block_error_zero()
    {
        var frame = BuildSyntheticSetResponse(
            srcMac: SampleDstMac, xid: 0xCAFEBABE,
            responseOption: 0x01, responseSub: 0x02, blockError: 0x00);
        var parsed = DcpFrame.TryParseSetResponse(frame, 0xCAFEBABE);
        parsed.Should().NotBeNull();
        parsed!.Success.Should().BeTrue();
        parsed.BlockError.Should().Be((byte)0);
        parsed.ResponseOption.Should().Be((byte)0x01);
        parsed.ResponseSubOption.Should().Be((byte)0x02);
        parsed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryParseSetResponse_failure_carries_error_message()
    {
        var frame = BuildSyntheticSetResponse(
            srcMac: SampleDstMac, xid: 1,
            responseOption: 0x01, responseSub: 0x02, blockError: 0x05);
        var parsed = DcpFrame.TryParseSetResponse(frame, 1);
        parsed.Should().NotBeNull();
        parsed!.Success.Should().BeFalse();
        parsed.BlockError.Should().Be((byte)0x05);
        parsed.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParseSetResponse_returns_null_on_xid_mismatch()
    {
        var frame = BuildSyntheticSetResponse(
            srcMac: SampleDstMac, xid: 1,
            responseOption: 0, responseSub: 0, blockError: 0);
        DcpFrame.TryParseSetResponse(frame, 2).Should().BeNull();
    }

    private static byte[] BuildSyntheticSetResponse(
        byte[] srcMac, uint xid,
        byte responseOption, byte responseSub, byte blockError)
    {
        // One Control/Response block: Option=0x05 Sub=0x04 Len=3
        //   [0]=responseOption [1]=responseSub [2]=blockError, then 1 byte pad (odd len).
        var blocks = new byte[4 + 3 + 1];
        blocks[0] = 0x05; blocks[1] = 0x04;
        BinaryPrimitives.WriteUInt16BigEndian(blocks.AsSpan(2, 2), 3);
        blocks[4] = responseOption;
        blocks[5] = responseSub;
        blocks[6] = blockError;
        // blocks[7] = 0 (pad)

        int totalLen = 14 + 12 + blocks.Length;
        var frame = new byte[Math.Max(60, totalLen)];
        Buffer.BlockCopy(srcMac, 0, frame, 6, 6);
        frame[12] = 0x88; frame[13] = 0x92;
        // DCP header: FrameID=0xFEFD, ServiceID=0x04 (Set), ServiceType=0x01 (Response)
        frame[14] = 0xFE; frame[15] = 0xFD;
        frame[16] = 0x04; frame[17] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(18, 4), xid);
        frame[22] = 0; frame[23] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), (ushort)blocks.Length);
        Buffer.BlockCopy(blocks, 0, frame, 26, blocks.Length);
        return frame;
    }

    private static byte[] BuildSyntheticResponse(
        byte[] srcMac, uint xid, string nameOfStation, string vendorName,
        ushort vendorId, ushort deviceId, byte deviceRole,
        byte[] ip, byte[] mask, byte[] gw)
    {
        // Build block list
        var blocks = new List<byte>();
        void AddBlock(byte option, byte sub, byte[] payload)
        {
            blocks.Add(option);
            blocks.Add(sub);
            // BlockInfo (2 bytes) + payload
            var value = new byte[2 + payload.Length];
            Buffer.BlockCopy(payload, 0, value, 2, payload.Length);
            blocks.Add((byte)(value.Length >> 8));
            blocks.Add((byte)(value.Length & 0xFF));
            blocks.AddRange(value);
            if ((value.Length & 1) == 1) blocks.Add(0); // pad
        }

        AddBlock(0x02, 0x01, System.Text.Encoding.ASCII.GetBytes(vendorName));
        AddBlock(0x02, 0x02, System.Text.Encoding.ASCII.GetBytes(nameOfStation));
        var devIdPayload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(devIdPayload.AsSpan(0), vendorId);
        BinaryPrimitives.WriteUInt16BigEndian(devIdPayload.AsSpan(2), deviceId);
        AddBlock(0x02, 0x03, devIdPayload);
        AddBlock(0x02, 0x04, new byte[] { deviceRole, 0 });
        var ipPayload = new byte[12];
        Buffer.BlockCopy(ip,   0, ipPayload, 0, 4);
        Buffer.BlockCopy(mask, 0, ipPayload, 4, 4);
        Buffer.BlockCopy(gw,   0, ipPayload, 8, 4);
        AddBlock(0x01, 0x02, ipPayload);

        int dcpDataLen = blocks.Count;
        int totalLen = 14 + 12 + dcpDataLen;
        var frame = new byte[Math.Max(60, totalLen)];

        // Ethernet header: dst=anything, src=srcMac, ethertype=0x8892
        // (dst doesn't matter for the parser)
        Buffer.BlockCopy(srcMac, 0, frame, 6, 6);
        frame[12] = 0x88; frame[13] = 0x92;

        // DCP header: FrameID=0xFEFF, ServiceID=0x05, ServiceType=0x01, Xid, ResponseDelay=0, DCPDataLen
        frame[14] = 0xFE; frame[15] = 0xFF;
        frame[16] = 0x05; frame[17] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(18, 4), xid);
        frame[22] = 0; frame[23] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(24, 2), (ushort)dcpDataLen);

        // Blocks
        Buffer.BlockCopy(blocks.ToArray(), 0, frame, 26, blocks.Count);
        return frame;
    }
}
