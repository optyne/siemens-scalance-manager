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
}
