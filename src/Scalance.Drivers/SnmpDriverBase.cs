using System.Net;
using Lextm.SharpSnmpLib;
using Scalance.Core.Abstractions;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;
using Scalance.Protocols.Snmp;

namespace Scalance.Drivers;

public abstract class SnmpDriverBase : IDeviceDriver
{
    protected SnmpClient? Snmp;
    protected Device? Device;

    public abstract DeviceModelKind Model { get; }
    public DeviceCapability Capabilities => CapabilityMatrix.For(Model);

    public virtual Task<OperationResult> ConnectAsync(Device device, Credential credential, CancellationToken ct = default)
    {
        try
        {
            Device = device;
            var endpoint = new IPEndPoint(IPAddress.Parse(device.Host), device.SnmpPort);
            Snmp = new SnmpClient(
                endpoint,
                device.SnmpVersion,
                credential.SnmpCommunityRead ?? "public",
                credential.SnmpCommunityWrite ?? "private",
                credential.SnmpV3);
            return Task.FromResult(OperationResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult.Fail($"SNMP connect failed: {ex.Message}", ex));
        }
    }

    public virtual async Task<OperationResult<DeviceStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        if (Snmp is null) return OperationResult<DeviceStatus>.Fail("Not connected.");
        try
        {
            var scalars = await Snmp.GetAsync(new[]
            {
                StandardOids.SysName,
                StandardOids.SysDescr,
                StandardOids.SysUpTime
            }, ct);

            var ports = await ReadPortsAsync(ct);
            var status = new DeviceStatus(
                SystemName: scalars[0].Data.ToString(),
                SystemDescription: scalars[1].Data.ToString(),
                Firmware: ExtractFirmware(scalars[1].Data.ToString()),
                Uptime: TimeSpanFromTicks(scalars[2].Data),
                Ports: ports);
            return OperationResult<DeviceStatus>.Ok(status);
        }
        catch (Exception ex)
        {
            return OperationResult<DeviceStatus>.Fail($"GetStatus failed: {ex.Message}", ex);
        }
    }

    protected async Task<IReadOnlyList<PortStatus>> ReadPortsAsync(CancellationToken ct)
    {
        if (Snmp is null) return Array.Empty<PortStatus>();
        var idx = await Snmp.WalkAsync(StandardOids.IfIndex, ct);
        var descr = await Snmp.WalkAsync(StandardOids.IfDescr, ct);
        var admin = await Snmp.WalkAsync(StandardOids.IfAdminStatus, ct);
        var oper = await Snmp.WalkAsync(StandardOids.IfOperStatus, ct);
        var speed = await Snmp.WalkAsync(StandardOids.IfSpeed, ct);
        var mac = await Snmp.WalkAsync(StandardOids.IfPhysAddress, ct);

        var list = new List<PortStatus>();
        for (int i = 0; i < idx.Count; i++)
        {
            list.Add(new PortStatus(
                Index: int.Parse(idx[i].Data.ToString()),
                Name: i < descr.Count ? descr[i].Data.ToString() : "",
                AdminUp: i < admin.Count && admin[i].Data.ToString() == "1",
                LinkUp: i < oper.Count && oper[i].Data.ToString() == "1",
                SpeedBps: i < speed.Count && long.TryParse(speed[i].Data.ToString(), out var s) ? s : 0,
                MacAddress: i < mac.Count ? mac[i].Data.ToString() : null));
        }
        return list;
    }

    private static TimeSpan TimeSpanFromTicks(ISnmpData data)
    {
        if (data is TimeTicks tt) return TimeSpan.FromMilliseconds(tt.ToUInt32() * 10.0);
        return TimeSpan.Zero;
    }

    private static string? ExtractFirmware(string sysDescr)
    {
        var idx = sysDescr.IndexOf("V", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? sysDescr.Substring(idx) : null;
    }

    public virtual Task<OperationResult<IReadOnlyList<Vlan>>> GetVlansAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Vlan>>.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult> SetVlansAsync(IReadOnlyList<Vlan> vlans, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult<NtpConfig>> GetNtpAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<NtpConfig>.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult> SetNtpAsync(NtpConfig config, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult<IReadOnlyList<InterfaceIpConfig>>> GetInterfacesAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<InterfaceIpConfig>>.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult> SetInterfaceAsync(InterfaceIpConfig config, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("Not implemented for this driver."));

    public virtual Task<OperationResult<IReadOnlyList<VpnTunnel>>> GetVpnTunnelsAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<VpnTunnel>>.Fail("VPN not supported by this driver."));

    public virtual Task<OperationResult> SetVpnTunnelAsync(VpnTunnel tunnel, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("VPN not supported by this driver."));

    public virtual Task<OperationResult<string>> BackupConfigAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<string>.Fail("Backup requires SSH/WBM driver."));

    public virtual Task<OperationResult> RestoreConfigAsync(string config, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("Restore requires SSH/WBM driver."));

    public virtual Task<OperationResult<IReadOnlyList<FirewallRule>>> ReadFirewallRulesAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<FirewallRule>>.Fail("此設備不支援防火牆功能。"));

    public virtual Task<OperationResult> WriteFirewallRuleAsync(FirewallRule rule, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援防火牆功能。"));

    public virtual Task<OperationResult<IReadOnlyList<PredefinedFirewallService>>> ReadPredefinedRulesAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<PredefinedFirewallService>>.Fail("此設備不支援防火牆功能。"));

    public virtual Task<OperationResult> WritePredefinedRuleAsync(PredefinedFirewallService service, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援防火牆功能。"));

    public virtual Task<OperationResult> SetAdminPasswordAsync(string username, string newPassword, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援透過 SSH-CLI 改密碼（需使用 WBM）。"));

    public virtual Task<OperationResult<DnsConfig>> GetDnsAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<DnsConfig>.Fail("此設備不支援 DNS client 設定。"));

    public virtual Task<OperationResult> SetDnsAsync(DnsConfig config, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 DNS client 設定。"));

    public virtual Task<OperationResult> ApplyBasicWizardAsync(BasicWizardConfig config, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 Basic Wizard（需使用 WBM）。"));

    public virtual Task<OperationResult> AddSyslogServerAsync(SyslogServer server, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 Syslog client 設定（需使用 WBM 或 CLI）。"));

    public virtual Task<OperationResult> RemoveSyslogServerAsync(SyslogServer server, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 Syslog client 設定（需使用 WBM 或 CLI）。"));

    public virtual Task<OperationResult> SetEventSeverityAsync(EventSink sink, EventSeverity level, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援事件嚴重性門檻（需要 SSH-CLI）。"));

    public virtual Task<OperationResult<string>> PingAsync(string host, PingOptions? options = null, CancellationToken ct = default)
        => Task.FromResult(OperationResult<string>.Fail("此設備不支援 CLI ping（僅 SSH-CLI 裝置可用）。"));

    public virtual Task<OperationResult<string>> TraceRouteAsync(string host, CancellationToken ct = default)
        => Task.FromResult(OperationResult<string>.Fail("此設備不支援 CLI traceroute（僅 SSH-CLI 裝置可用）。"));

    public virtual Task<OperationResult<IReadOnlyList<string>>> ListConfigBackupsAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<string>>.Fail("此設備不支援 configbackup（僅 SSH-CLI 裝置可用）。"));
    public virtual Task<OperationResult> CreateConfigBackupAsync(string name, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 configbackup（僅 SSH-CLI 裝置可用）。"));
    public virtual Task<OperationResult> RestoreConfigBackupAsync(string name, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 configbackup（僅 SSH-CLI 裝置可用）。"));
    public virtual Task<OperationResult> DeleteConfigBackupAsync(string name, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 configbackup（僅 SSH-CLI 裝置可用）。"));

    public virtual Task<OperationResult> SetSnmpAgentEnabledAsync(bool enabled, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 snmpagent 寫入（需要 SSH-CLI）。"));
    public virtual Task<OperationResult> SetSnmpAgentVersionAsync(SnmpAgentVersionPolicy policy, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 snmp agent version 寫入（需要 SSH-CLI）。"));
    public virtual Task<OperationResult> SetSnmpAgentPortAsync(int port, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 snmpagent port 寫入（需要 SSH-CLI）。"));

    public virtual Task<OperationResult> RestartAsync(RestartMode mode, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Fail("此設備不支援 CLI restart（僅 SSH-CLI 裝置可用）。"));

    public virtual async ValueTask DisposeAsync()
    {
        if (Snmp is not null) await Snmp.DisposeAsync();
    }
}
