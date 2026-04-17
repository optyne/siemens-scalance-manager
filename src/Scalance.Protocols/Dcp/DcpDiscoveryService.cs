using System.Collections.Concurrent;
using System.Net;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Scalance.Protocols.Dcp;

/// <summary>
/// Raw-socket PROFINET DCP Identify-All discovery via Npcap / libpcap.
///
/// Requires Npcap (https://npcap.com) or WinPcap to be installed on the host.
/// The service catches DllNotFoundException / PcapException when the native
/// layer is missing and reports it via <see cref="NpcapMissing"/>.
/// </summary>
public sealed class DcpDiscoveryService
{
    /// <summary>
    /// True if we couldn't even initialize libpcap. The caller should show an
    /// explanatory message pointing the user at Npcap.
    /// </summary>
    public bool NpcapMissing { get; private set; }

    /// <summary>Enumerate available capture devices. Empty when Npcap is missing.</summary>
    public IReadOnlyList<DcpCaptureAdapter> ListAdapters()
    {
        try
        {
            var devices = CaptureDeviceList.Instance;
            return devices.OfType<LibPcapLiveDevice>()
                .Select(d => new DcpCaptureAdapter(d.Name, d.Description ?? d.Name, MacOf(d)))
                .ToList();
        }
        catch (DllNotFoundException) { NpcapMissing = true; return Array.Empty<DcpCaptureAdapter>(); }
        catch (PcapException)          { NpcapMissing = true; return Array.Empty<DcpCaptureAdapter>(); }
        catch (TypeInitializationException) { NpcapMissing = true; return Array.Empty<DcpCaptureAdapter>(); }
    }

    /// <summary>
    /// Send one Identify-All request on <paramref name="adapterName"/> and collect
    /// responses for <paramref name="timeout"/>. Safe to call from a background task.
    /// </summary>
    public async Task<IReadOnlyList<DcpIdentifyResponse>> DiscoverAsync(
        string adapterName, TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, DcpIdentifyResponse>();

        LibPcapLiveDevice? dev = null;
        try
        {
            dev = CaptureDeviceList.Instance
                .OfType<LibPcapLiveDevice>()
                .FirstOrDefault(d => d.Name == adapterName)
                ?? throw new InvalidOperationException($"Adapter '{adapterName}' not found.");

            dev.Open(DeviceModes.Promiscuous, 500);

            var xid = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
            var srcMac = MacOf(dev) ?? throw new InvalidOperationException("Adapter has no MAC address.");
            var req = DcpFrame.BuildIdentifyAllRequest(srcMac, xid);

            // BPF filter: only PROFINET ethertype 0x8892.
            dev.Filter = "ether proto 0x8892";

            void OnPacket(object s, PacketCapture e)
            {
                try
                {
                    var frame = e.Data;
                    var rsp = DcpFrame.TryParseIdentifyResponse(frame, xid);
                    if (rsp is not null) results[rsp.SourceMac] = rsp;
                }
                catch { /* swallow malformed frames */ }
            }

            dev.OnPacketArrival += OnPacket;
            dev.StartCapture();
            try
            {
                dev.SendPacket(req);
                try { await Task.Delay(timeout, ct); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                try { dev.StopCapture(); } catch { }
                dev.OnPacketArrival -= OnPacket;
            }
        }
        catch (DllNotFoundException)    { NpcapMissing = true; }
        catch (PcapException)           { NpcapMissing = true; }
        catch (TypeInitializationException) { NpcapMissing = true; }
        finally
        {
            try { dev?.Close(); } catch { }
        }

        return results.Values.OrderBy(r => r.NameOfStation ?? r.SourceMac).ToList();
    }

    /// <summary>
    /// Send a DCP Set-IP request to one device (unicast) and wait up to
    /// <paramref name="timeout"/> for its response. Returns null when Npcap is
    /// missing or no matching response arrives in time.
    /// </summary>
    public Task<DcpSetResponse?> SetIpAsync(
        string adapterName, byte[] dstMac,
        IPAddress ip, IPAddress mask, IPAddress gateway, bool savePermanent,
        TimeSpan timeout, CancellationToken ct = default)
        => SendAndAwaitAsync(adapterName, timeout, ct,
            (srcMac, xid) => DcpFrame.BuildSetIpRequest(srcMac, dstMac, xid, ip, mask, gateway, savePermanent));

    /// <summary>Trigger the "flash LED" signal on one device.</summary>
    public Task<DcpSetResponse?> FlashLedAsync(
        string adapterName, byte[] dstMac, TimeSpan timeout, CancellationToken ct = default)
        => SendAndAwaitAsync(adapterName, timeout, ct,
            (srcMac, xid) => DcpFrame.BuildFlashLedRequest(srcMac, dstMac, xid));

    private async Task<DcpSetResponse?> SendAndAwaitAsync(
        string adapterName, TimeSpan timeout, CancellationToken ct,
        Func<byte[], uint, byte[]> buildFrame)
    {
        DcpSetResponse? result = null;
        LibPcapLiveDevice? dev = null;
        try
        {
            dev = CaptureDeviceList.Instance
                .OfType<LibPcapLiveDevice>()
                .FirstOrDefault(d => d.Name == adapterName)
                ?? throw new InvalidOperationException($"Adapter '{adapterName}' not found.");

            dev.Open(DeviceModes.Promiscuous, 500);

            var xid = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
            var srcMac = MacOf(dev) ?? throw new InvalidOperationException("Adapter has no MAC address.");
            var req = buildFrame(srcMac, xid);

            dev.Filter = "ether proto 0x8892";

            var tcs = new TaskCompletionSource<DcpSetResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPacket(object s, PacketCapture e)
            {
                try
                {
                    var parsed = DcpFrame.TryParseSetResponse(e.Data, xid);
                    if (parsed is not null) tcs.TrySetResult(parsed);
                }
                catch { /* swallow malformed frames */ }
            }

            dev.OnPacketArrival += OnPacket;
            dev.StartCapture();
            try
            {
                dev.SendPacket(req);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(timeout);
                try { result = await tcs.Task.WaitAsync(linked.Token); }
                catch (OperationCanceledException) { result = null; }
            }
            finally
            {
                try { dev.StopCapture(); } catch { }
                dev.OnPacketArrival -= OnPacket;
            }
        }
        catch (DllNotFoundException)        { NpcapMissing = true; }
        catch (PcapException)               { NpcapMissing = true; }
        catch (TypeInitializationException) { NpcapMissing = true; }
        finally
        {
            try { dev?.Close(); } catch { }
        }

        return result;
    }

    /// <summary>Parse a MAC string formatted as XX:XX:XX:XX:XX:XX or XX-XX-XX-XX-XX-XX.</summary>
    public static byte[] ParseMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) throw new ArgumentException("MAC is empty.", nameof(mac));
        var parts = mac.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) throw new FormatException($"Invalid MAC '{mac}'.");
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++) bytes[i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }

    private static byte[]? MacOf(LibPcapLiveDevice dev)
    {
        try
        {
            var mac = dev.MacAddress;
            return mac?.GetAddressBytes();
        }
        catch { return null; }
    }
}

public sealed record DcpCaptureAdapter(string Name, string Description, byte[]? Mac)
{
    public string MacDisplay => Mac is null ? "-" : string.Join(":", Mac.Select(b => b.ToString("X2")));
}
