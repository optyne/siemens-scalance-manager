namespace Scalance.Core.Models;

public sealed class DnsConfig
{
    /// <summary>
    /// Manually configured DNS server IPs (max 3 per S615 CLI manual p. 414).
    /// Used together with <see cref="ServerType"/>: when ServerType=Manual the
    /// device only resolves via these; when ServerType=All it ALSO uses any
    /// DHCP-learned servers in addition to these.
    /// </summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>Search domain (e.g. corp.example.com).</summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Whether the DNS client is enabled. Maps to `no shutdown` / `shutdown`
    /// in `dnsclient` config mode (S615 CLI manual sec 9.7.3.4 p. 416 — note
    /// that section's Description text is wrong; the Result paragraph is
    /// authoritative: `shutdown` disables, `no shutdown` enables). Default
    /// is true so a fresh model behaves like the device's factory default.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// DNS server selection mode (S615 CLI manual sec 9.7 `server type
    /// {all|manual|...}`). Real-device default on V08 firmware is
    /// <see cref="DnsServerType.All"/> (DHCP-learned + manual). Setting
    /// <see cref="DnsServerType.Manual"/> uses ONLY the manual list.
    /// </summary>
    public DnsServerType ServerType { get; set; } = DnsServerType.All;
}

/// <summary>
/// DNS server selection mode for `server type` command in dnsclient
/// config mode. Manual lists more options (e.g. `dhcp`) — extend if
/// needed; All and Manual are the two practical modes for S615.
/// </summary>
public enum DnsServerType
{
    /// <summary>`server type all` — use both DHCP-learned and manually
    /// configured servers (factory default on S615 V08).</summary>
    All,
    /// <summary>`server type manual` — use only manually configured
    /// servers (the App's prior hard-coded behaviour).</summary>
    Manual,
}
