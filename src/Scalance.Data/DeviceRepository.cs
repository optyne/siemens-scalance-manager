using Microsoft.EntityFrameworkCore;
using Scalance.Core.Models;
using Scalance.Data.Entities;

namespace Scalance.Data;

public sealed class DeviceRepository
{
    private readonly IDbContextFactory<ScalanceDbContext> _factory;

    public DeviceRepository(IDbContextFactory<ScalanceDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return rows.Select(MapFromEntity).ToList();
    }

    public async Task<Device?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Devices.FindAsync(new object?[] { id }, ct);
        return row is null ? null : MapFromEntity(row);
    }

    public async Task UpsertAsync(Device device, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Devices.FindAsync(new object?[] { device.Id }, ct);
        if (existing is null)
        {
            db.Devices.Add(MapToEntity(device));
        }
        else
        {
            CopyInto(existing, device);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Devices.FindAsync(new object?[] { id }, ct);
        if (row is not null)
        {
            db.Devices.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    private static Device MapFromEntity(DeviceEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Description = e.Description,
        Model = e.Model,
        Host = e.Host,
        SnmpPort = e.SnmpPort,
        SshPort = e.SshPort,
        HttpsPort = e.HttpsPort,
        SnmpVersion = e.SnmpVersion,
        PreferredProtocol = e.PreferredProtocol,
        CredentialId = e.CredentialId,
        Tags = e.Tags,
        CreatedAt = e.CreatedAt,
        LastSeenAt = e.LastSeenAt,
        LastKnownFirmware = e.LastKnownFirmware
    };

    private static DeviceEntity MapToEntity(Device d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        Model = d.Model,
        Host = d.Host,
        SnmpPort = d.SnmpPort,
        SshPort = d.SshPort,
        HttpsPort = d.HttpsPort,
        SnmpVersion = d.SnmpVersion,
        PreferredProtocol = d.PreferredProtocol,
        CredentialId = d.CredentialId,
        Tags = d.Tags,
        CreatedAt = d.CreatedAt,
        LastSeenAt = d.LastSeenAt,
        LastKnownFirmware = d.LastKnownFirmware
    };

    private static void CopyInto(DeviceEntity dst, Device src)
    {
        dst.Name = src.Name;
        dst.Description = src.Description;
        dst.Model = src.Model;
        dst.Host = src.Host;
        dst.SnmpPort = src.SnmpPort;
        dst.SshPort = src.SshPort;
        dst.HttpsPort = src.HttpsPort;
        dst.SnmpVersion = src.SnmpVersion;
        dst.PreferredProtocol = src.PreferredProtocol;
        dst.CredentialId = src.CredentialId;
        dst.Tags = src.Tags;
        dst.LastSeenAt = src.LastSeenAt;
        dst.LastKnownFirmware = src.LastKnownFirmware;
    }
}
