namespace Scalance.Core.Models;

public enum FirewallAction { Accept, Drop, Reject }

public sealed class FirewallRule
{
    public int Index { get; set; }
    public bool Enabled { get; set; } = true;
    public FirewallAction Action { get; set; } = FirewallAction.Accept;
    // S615 CLI manual sec 12.3.4.31 p. 627: iftype + ifstring are separate
    // tokens, e.g. `from vlan 1 to vlan 2`. `vlan1` (no space) is rejected
    // — see manual examples p. 65, 430 (`int vlan 1`).
    public string From { get; set; } = "vlan 1";
    public string To { get; set; } = "vlan 2";
    public string SourceCidr { get; set; } = "";
    public string DestinationCidr { get; set; } = "";
    public string Service { get; set; } = "All";
    public bool Log { get; set; }
}

public sealed class PredefinedFirewallService
{
    public string ServiceName { get; set; } = "";
    public bool LocalAccess { get; set; }
    public bool ExternalAccess { get; set; }
}
