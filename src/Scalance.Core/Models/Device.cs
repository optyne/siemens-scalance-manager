using System.Net;

namespace Scalance.Core.Models;

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DeviceModelKind Model { get; set; } = DeviceModelKind.Unknown;
    public string Host { get; set; } = "";
    public int SnmpPort { get; set; } = 161;
    public int SshPort { get; set; } = 22;
    public int HttpsPort { get; set; } = 443;
    public SnmpVersion SnmpVersion { get; set; } = SnmpVersion.V2c;
    public ProtocolKind PreferredProtocol { get; set; } = ProtocolKind.Snmp;
    public Guid? CredentialId { get; set; }
    public string? Tags { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastKnownFirmware { get; set; }

    public IPEndPoint SnmpEndpoint() =>
        new(IPAddress.TryParse(Host, out var ip) ? ip : Dns.GetHostAddresses(Host)[0], SnmpPort);
}
