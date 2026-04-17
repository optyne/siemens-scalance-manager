using Scalance.Core.Models;

namespace Scalance.Data.Entities;

public class DeviceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DeviceModelKind Model { get; set; }
    public string Host { get; set; } = "";
    public int SnmpPort { get; set; } = 161;
    public int SshPort { get; set; } = 22;
    public int HttpsPort { get; set; } = 443;
    public SnmpVersion SnmpVersion { get; set; }
    public ProtocolKind PreferredProtocol { get; set; }
    public Guid? CredentialId { get; set; }
    public string? Tags { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastKnownFirmware { get; set; }
}

public class CredentialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public byte[] EncryptedBlob { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ConfigSnapshotEntity
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? Note { get; set; }
}

public class AuditLogEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? DeviceId { get; set; }
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string? Details { get; set; }
    public string? User { get; set; }
}
