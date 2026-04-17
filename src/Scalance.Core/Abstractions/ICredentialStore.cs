using Scalance.Core.Models;

namespace Scalance.Core.Abstractions;

public interface ICredentialStore
{
    Task<Credential?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Guid> SaveAsync(Credential credential, string? name = null, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid id, Credential credential, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<(Guid Id, string Name)>> ListAsync(CancellationToken ct = default);
}
