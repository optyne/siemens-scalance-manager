namespace Scalance.Core.Models;

public sealed class BasicWizardConfig
{
    public string? Hostname { get; set; }
    public InterfaceIpConfig? Interface { get; set; }
    public DnsConfig? Dns { get; set; }
    public NtpConfig? Ntp { get; set; }

    /// <summary>New admin password. When set, also supply <see cref="AdminUsername"/>.</summary>
    public string? NewAdminPassword { get; set; }
    public string AdminUsername { get; set; } = "admin";
}
