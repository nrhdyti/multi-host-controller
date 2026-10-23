using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;
using MultiHost.Shared;

namespace MultiHost.Master.Services;

/// <summary>Marks silent hosts offline, times out stuck jobs and prunes old metric samples.</summary>
public class HostMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MonitoringOptions _opt;
    private readonly ILogger<HostMonitorService> _log;

    public HostMonitorService(IServiceScopeFactory scopes, IOptions<MonitoringOptions> opt, ILogger<HostMonitorService> log)
    { _scopes = scopes; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled) return;
        var lastPrune = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
                var notifier = scope.ServiceProvider.GetRequiredService<IStatusNotifier>();
                var now = DateTime.UtcNow;

                var cutoff = now.AddSeconds(-_opt.OfflineAfterSeconds);
                var stale = await db.Hosts.Where(h => h.Status == HostStatus.Online && h.LastHeartbeatAt < cutoff).ToListAsync(stoppingToken);
                foreach (var h in stale) { h.Status = HostStatus.Offline; _log.LogWarning("Host {Host} went offline", h.Hostname); }

                var jobCutoff = now.AddMinutes(-_opt.JobTimeoutMinutes);
                var stuck = await db.DeploymentJobs
                    .Where(j => (j.Status == JobStatus.Dispatched || j.Status == JobStatus.Running) && j.LastUpdateAt < jobCutoff)
                    .ToListAsync(stoppingToken);
                foreach (var j in stuck)
                {
                    j.Status = JobStatus.Failed; j.Message = "Timed out waiting for the agent."; j.FinishedAt = now;
                    _log.LogWarning("Job {Job} timed out", j.Id);
                }

                if (stale.Count > 0 || stuck.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                    await notifier.HostsChangedAsync();
                    foreach (var id in stuck.Select(j => j.DeploymentId).Distinct()) await notifier.DeploymentChangedAsync(id);
                }

                if (now - lastPrune > TimeSpan.FromHours(1))
                {
                    var keep = now.AddHours(-_opt.MetricRetentionHours);
                    await db.Samples.Where(s => s.At < keep).ExecuteDeleteAsync(stoppingToken);
                    lastPrune = now;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Host monitor iteration failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(_opt.CheckIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public class AdminSeeder
{
    private readonly MasterDbContext _db;
    private readonly AdminOptions _opt;
    private readonly ILogger<AdminSeeder> _log;
    public AdminSeeder(MasterDbContext db, IOptions<AdminOptions> opt, ILogger<AdminSeeder> log) { _db = db; _opt = opt.Value; _log = log; }

    public async Task SeedAsync()
    {
        if (await _db.AdminUsers.AnyAsync(u => u.Username == _opt.Username)) return;
        var password = _opt.Password;
        if (string.IsNullOrEmpty(password))
        {
            password = Crypto.RandomToken(12);
            _log.LogWarning("No Admin:Password configured. Generated one-time password for '{User}': {Password}", _opt.Username, password);
        }
        _db.AdminUsers.Add(new AdminUser { Id = Guid.NewGuid(), Username = _opt.Username, PasswordHash = Crypto.HashPassword(password) });
        await _db.SaveChangesAsync();
    }
}
