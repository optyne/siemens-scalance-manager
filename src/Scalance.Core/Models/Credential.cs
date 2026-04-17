namespace Scalance.Core.Models;

public sealed record Credential(
    string? Username,
    string? Password,
    string? PrivateKeyPath,
    string? SnmpCommunityRead,
    string? SnmpCommunityWrite,
    SnmpV3Credential? SnmpV3)
{
    public static Credential Empty { get; } = new(null, null, null, null, null, null);
}

public sealed record SnmpV3Credential(
    string Username,
    string AuthPassword,
    string PrivPassword,
    SnmpV3AuthProtocol AuthProtocol = SnmpV3AuthProtocol.Sha,
    SnmpV3PrivProtocol PrivProtocol = SnmpV3PrivProtocol.Aes128);

public enum SnmpV3AuthProtocol { None, Md5, Sha, Sha256, Sha512 }
public enum SnmpV3PrivProtocol { None, Des, Aes128, Aes192, Aes256 }
