using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scalance.Core.Abstractions;
using Scalance.Core.Models;
using Scalance.Data.Entities;

namespace Scalance.Data;

public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly IDbContextFactory<ScalanceDbContext> _factory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SiemensScalanceManager-v1");

    public DpapiCredentialStore(IDbContextFactory<ScalanceDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<Credential?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Credentials.FindAsync(new object?[] { id }, ct);
        if (row is null) return null;
        var plain = Unprotect(row.EncryptedBlob);
        return JsonSerializer.Deserialize<Credential>(plain);
    }

    public async Task<Guid> SaveAsync(Credential credential, string? name = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential);
        var blob = Protect(json);
        var entity = new CredentialEntity
        {
            Name = name ?? credential.Username ?? "unnamed",
            EncryptedBlob = blob
        };
        db.Credentials.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, Credential credential, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Credentials.FindAsync(new object?[] { id }, ct);
        if (row is null) return false;
        var json = JsonSerializer.SerializeToUtf8Bytes(credential);
        row.EncryptedBlob = Protect(json);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Credentials.FindAsync(new object?[] { id }, ct);
        if (row is not null)
        {
            db.Credentials.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<(Guid Id, string Name)>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var items = await db.Credentials
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        return items.Select(i => (i.Id, i.Name)).ToList();
    }

    private static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
