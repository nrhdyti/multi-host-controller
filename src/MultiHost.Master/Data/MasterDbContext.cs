using Microsoft.EntityFrameworkCore;

namespace MultiHost.Master.Data;

public class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options) { }

    public DbSet<ManagedHost> Hosts => Set<ManagedHost>();
    public DbSet<MetricSample> Samples => Set<MetricSample>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<ApplicationPackage> Applications => Set<ApplicationPackage>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentJob> DeploymentJobs => Set<DeploymentJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ManagedHost>(e =>
        {
            e.HasIndex(x => x.Hostname).IsUnique();
            e.Property(x => x.Hostname).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Samples).WithOne().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MetricSample>().HasIndex(x => new { x.HostId, x.At });
        b.Entity<EnrollmentToken>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<AdminUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<ApplicationPackage>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Deployment>(e =>
        {
            e.HasOne(d => d.Application).WithMany().HasForeignKey(d => d.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(d => d.Jobs).WithOne(j => j.Deployment).HasForeignKey(j => j.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.CreatedAt);
        });
        b.Entity<DeploymentJob>(e =>
        {
            e.HasOne<ManagedHost>().WithMany().HasForeignKey(j => j.HostId).OnDelete(DeleteBehavior.SetNull);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(24);
            e.HasIndex(j => new { j.HostId, j.Status });
        });
    }
}
