using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.Core.Abstractions;

public interface IDeviceDriver : IAsyncDisposable
{
    DeviceModelKind Model { get; }
    DeviceCapability Capabilities { get; }

    Task<OperationResult> ConnectAsync(Device device, Credential credential, CancellationToken ct = default);
    Task<OperationResult<DeviceStatus>> GetStatusAsync(CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<Vlan>>> GetVlansAsync(CancellationToken ct = default);
    Task<OperationResult> SetVlansAsync(IReadOnlyList<Vlan> vlans, CancellationToken ct = default);

    Task<OperationResult<NtpConfig>> GetNtpAsync(CancellationToken ct = default);
    Task<OperationResult> SetNtpAsync(NtpConfig config, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<InterfaceIpConfig>>> GetInterfacesAsync(CancellationToken ct = default);
    Task<OperationResult> SetInterfaceAsync(InterfaceIpConfig config, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<VpnTunnel>>> GetVpnTunnelsAsync(CancellationToken ct = default);
    Task<OperationResult> SetVpnTunnelAsync(VpnTunnel tunnel, CancellationToken ct = default);

    Task<OperationResult<string>> BackupConfigAsync(CancellationToken ct = default);
    Task<OperationResult> RestoreConfigAsync(string config, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<FirewallRule>>> ReadFirewallRulesAsync(CancellationToken ct = default);
    Task<OperationResult> WriteFirewallRuleAsync(FirewallRule rule, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<PredefinedFirewallService>>> ReadPredefinedRulesAsync(CancellationToken ct = default);
    Task<OperationResult> WritePredefinedRuleAsync(PredefinedFirewallService service, CancellationToken ct = default);

    Task<OperationResult> SetAdminPasswordAsync(string username, string newPassword, CancellationToken ct = default);
    Task<OperationResult<DnsConfig>> GetDnsAsync(CancellationToken ct = default);
    Task<OperationResult> SetDnsAsync(DnsConfig config, CancellationToken ct = default);
    Task<OperationResult> ApplyBasicWizardAsync(BasicWizardConfig config, CancellationToken ct = default);

    // Syslog — PH_SCALANCE-S615-CLI_76 sec 13.2 pp. 822-825.
    Task<OperationResult> AddSyslogServerAsync(SyslogServer server, CancellationToken ct = default);
    Task<OperationResult> RemoveSyslogServerAsync(SyslogServer server, CancellationToken ct = default);

    // Diagnostics: ping — PH_SCALANCE-S615-CLI_76 sec 5.1.8 p. 85-86.
    // Returns the raw CLI output so the UI can show per-packet RTT / loss.
    Task<OperationResult<string>> PingAsync(string host, PingOptions? options = null, CancellationToken ct = default);
}

public interface IDeviceDriverFactory
{
    IDeviceDriver Create(DeviceModelKind model);
}
