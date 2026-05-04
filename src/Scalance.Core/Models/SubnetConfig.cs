namespace Scalance.Core.Models;

public sealed class InterfaceIpConfig
{
    public string InterfaceName { get; set; } = "";
    public bool DhcpEnabled { get; set; }
    public string? IpAddress { get; set; }
    public string? SubnetMask { get; set; }
    public int? PrefixLength { get; set; }
    public string? DefaultGateway { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public int? VlanId { get; set; }

    /// <summary>
    /// Friendly interface name set via `alias <name>` (S615 CLI manual sec
    /// 5.1.12.1 p. 99). Pure metadata — has no effect on configuration but
    /// the device persists it in running-config and shows it in WBM. We
    /// preserve it on read-modify-write so the operator's labels survive.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Whether this VLAN interface is the device's TIA interface
    /// (PROFINET / DCP discovery target). Set with the `tia-interface`
    /// command (S615 CLI manual sec 8.x — only ONE VLAN interface can be
    /// the TIA interface at a time; setting it on one disables it on
    /// every other VLAN interface). Only meaningful for `vlan N`
    /// interfaces; ignored on `ppp N` and physical ports.
    /// </summary>
    public bool TiaInterface { get; set; }
}
