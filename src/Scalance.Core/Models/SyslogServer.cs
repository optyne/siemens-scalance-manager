namespace Scalance.Core.Models;

/// <summary>
/// A Syslog server entry for the S615 / X-200 Syslog client.
/// Verified against PH_SCALANCE-S615-CLI_76 sec 13.2.2.1 p. 824:
///   syslogserver { ipv4 &lt;ucast_addr&gt; | fqdn-name &lt;FQDN&gt; | ipv6 &lt;ip6_addr&gt; }
///                [&lt;port(1-65535)&gt;] [tls]
/// </summary>
public sealed class SyslogServer
{
    /// <summary>IPv4 dotted-quad, IPv6 literal, or FQDN (max 100 chars per manual p. 824).</summary>
    public string Host { get; set; } = "";

    /// <summary>UDP/TCP port. null = manual default (514).</summary>
    public int? Port { get; set; }

    /// <summary>true = append `tls` keyword for encrypted transport.</summary>
    public bool UseTls { get; set; }
}
