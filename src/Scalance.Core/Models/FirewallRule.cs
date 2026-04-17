namespace Scalance.Core.Models;

public enum FirewallAction { Accept, Drop, Reject }

public sealed class FirewallRule
{
    public int Index { get; set; }
    public bool Enabled { get; set; } = true;
    public FirewallAction Action { get; set; } = FirewallAction.Accept;
    public string From { get; set; } = "vlan1";
    public string To { get; set; } = "vlan2";
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
