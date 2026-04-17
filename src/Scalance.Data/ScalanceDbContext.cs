using Microsoft.EntityFrameworkCore;
using Scalance.Data.Entities;

namespace Scalance.Data;

public class ScalanceDbContext : DbContext
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<ConfigSnapshotEntity> ConfigSnapshots => Set<ConfigSnapshotEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    public ScalanceDbContext(DbContextOptions<ScalanceDbContext> opts) : base(opts) { }

    public static string DefaultDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SiemensScalanceManager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "scalance.db");
    }

    public static DbContextOptions<ScalanceDbContext> BuildOptions(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath();
        return new DbContextOptionsBuilder<ScalanceDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeviceEntity>().HasIndex(d => d.Name);
        b.Entity<CredentialEntity>().HasIndex(c => c.Name);
        b.Entity<ConfigSnapshotEntity>().HasIndex(c => new { c.DeviceId, c.CapturedAt });
        b.Entity<AuditLogEntity>().HasIndex(a => a.At);
    }
}
