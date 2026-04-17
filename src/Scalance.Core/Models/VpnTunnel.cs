namespace Scalance.Core.Models;

public sealed class VpnTunnel
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string RemoteEndpoint { get; set; } = "";
    public string LocalSubnet { get; set; } = "";
    public string RemoteSubnet { get; set; } = "";
    public VpnAuthMode AuthMode { get; set; } = VpnAuthMode.Psk;
    public string? PreSharedKey { get; set; }
    public string? LocalCertificateName { get; set; }
    public IkeSettings Ike { get; set; } = new();
    public EspSettings Esp { get; set; } = new();
}

public enum VpnAuthMode { Psk, Certificate }

// NOTE: per S615 CLI manual sec 12.4.7 p. 744 and sec 12.4.8 p. 752, IPsec
// lifetimes are in MINUTES, not seconds. Defaults below pick manual-aligned
// values (phase 1: 480 min = 8h; phase 2: 60 min = 1h).
public sealed class IkeSettings
{
    public string Encryption { get; set; } = "aes256cbc";
    public string Hash { get; set; } = "sha256";
    public string DhGroup { get; set; } = "14";
    public int LifetimeMinutes { get; set; } = 480;
}

public sealed class EspSettings
{
    public string Encryption { get; set; } = "aes256cbc";
    public string Hash { get; set; } = "sha256";
    public string? PfsGroup { get; set; } = "14";
    public int LifetimeMinutes { get; set; } = 60;
}
