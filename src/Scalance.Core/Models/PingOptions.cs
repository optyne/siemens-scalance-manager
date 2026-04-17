namespace Scalance.Core.Models;

/// <summary>
/// Options for <c>IDeviceDriver.PingAsync</c>. Ranges verified against
/// PH_SCALANCE-S615-CLI_76 sec 5.1.8 p. 86:
///   ping { &lt;destination-address&gt; | fqdn-name &lt;FQDN&gt; }
///        [size &lt;byte(0-2080)&gt;] [count &lt;packet_count(1-10)&gt;]
///        [timeout &lt;seconds(1-100)&gt;]
/// </summary>
public sealed class PingOptions
{
    /// <summary>Packet size in bytes. null = device default (32). Range 0-2080.</summary>
    public int? SizeBytes { get; set; }

    /// <summary>Number of echo requests. null = device default (3). Range 1-10.</summary>
    public int? Count { get; set; }

    /// <summary>Reply timeout in seconds. null = device default (1). Range 1-100.</summary>
    public int? TimeoutSeconds { get; set; }
}
