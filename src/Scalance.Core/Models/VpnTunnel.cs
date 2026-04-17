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

public sealed class IkeSettings
{
    public string Encryption { get; set; } = "aes256";
    public string Hash { get; set; } = "sha256";
    public string DhGroup { get; set; } = "14";
    public int LifetimeSeconds { get; set; } = 28800;
}

public sealed class EspSettings
{
    public string Encryption { get; set; } = "aes256";
    public string Hash { get; set; } = "sha256";
    public string? PfsGroup { get; set; } = "14";
    public int LifetimeSeconds { get; set; } = 3600;
}
