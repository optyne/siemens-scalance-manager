namespace Scalance.Core.Models;

public sealed class DnsConfig
{
    public List<string> Servers { get; set; } = new();
    public string? DomainName { get; set; }
}
