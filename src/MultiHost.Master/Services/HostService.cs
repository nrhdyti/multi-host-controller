using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using MultiHost.Shared;

namespace MultiHost.Master.Services;

public interface IHostService
{
    Task<RegisterAgentResponse?> RegisterAsync(RegisterAgentRequest req, CancellationToken ct);
    Task<bool> HeartbeatAsync(Guid hostId, HeartbeatRequest hb, CancellationToken ct);
    Task<bool> DeregisterAsync(Guid hostId, CancellationToken ct);
    Task<List<HostDto>> ListAsync(CancellationToken ct);
    Task<HostDto?> GetAsync(Guid id, CancellationToken ct);
    Task<List<MetricSampleDto>> MetricsAsync(Guid id, int minutes, CancellationToken ct);
    Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(CreateEnrollmentTokenRequest req, CancellationToken ct);
}

public class HostService : IHostService
{
    private readonly MasterDbContext _db;
    private readonly TokenService _tokens;
    private readonly SecurityOptions _sec;
    private readonly MonitoringOptions _mon;
    private readonly IStatusNotifier _notifier;
    private readonly ILogger<HostService> _log;

    public HostService(MasterDbContext db, TokenService tokens, IOptions<SecurityOptions> sec,
        IOptions<MonitoringOptions> mon, IStatusNotifier notifier, ILogger<HostService> log)
    {
        _db = db; _tokens = tokens; _sec = sec.Value; _mon = mon.Value; _notifier = notifier; _log = log;
    }

    public async Task<RegisterAgentResponse?> RegisterAsync(RegisterAgentRequest req, CancellationToken ct)
    {
        if (!await ConsumeEnrollmentTokenAsync(req.EnrollmentToken, ct))
        {
            _log.LogWarning("Registration rejected for {Hostname}: invalid enrollment token", req.Hostname);
            return null;
        }

        // Hostname is unique: re-registering the same host re-issues credentials instead of creating duplicates.
        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Hostname == req.Hostname, ct);
        if (host is null)
        {
            host = new ManagedHost { Id = Guid.NewGuid(), Hostname = req.Hostname, RegisteredAt = DateTime.UtcNow };
            _db.Hosts.Add(host);
        }
        host.IpAddress = req.IpAddress;
        host.Os = req.Os;
        host.Architecture = req.Architecture;
        host.AgentVersion = req.AgentVersion;
        host.Capabilities = NormalizeCaps(req.Capabilities);
        host.Status = HostStatus.Online;
        host.LastHeartbeatAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Host {Hostname} registered as {HostId}", host.Hostname, host.Id);
        await _notifier.HostsChangedAsync();
        return new RegisterAgentResponse
        {
            HostId = host.Id,
            Token = _tokens.CreateAgentToken(host.Id),
            HeartbeatIntervalSeconds = _mon.HeartbeatIntervalSeconds
        };
    }

    public async Task<bool> HeartbeatAsync(Guid hostId, HeartbeatRequest hb, CancellationToken ct)
    {
        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId, ct);
        if (host is null) return false;

        var now = DateTime.UtcNow;
        host.Status = HostStatus.Online;
        host.LastHeartbeatAt = now;
        host.CpuPercent = hb.CpuPercent;
        host.MemoryUsedMb = hb.MemoryUsedMb;
        host.MemoryTotalMb = hb.MemoryTotalMb;
        host.DiskUsedGb = hb.DiskUsedGb;
        host.DiskTotalGb = hb.DiskTotalGb;
        if (!string.IsNullOrWhiteSpace(hb.IpAddress)) host.IpAddress = hb.IpAddress;
        if (hb.Capabilities is not null) host.Capabilities = NormalizeCaps(hb.Capabilities);

        _db.Samples.Add(new MetricSample
        {
            HostId = hostId, At = now, Cpu = hb.CpuPercent, MemUsedMb = hb.MemoryUsedMb, MemTotalMb = hb.MemoryTotalMb,
            DiskUsedGb = hb.DiskUsedGb, DiskTotalGb = hb.DiskTotalGb
        });
        await _db.SaveChangesAsync(ct);
        await _notifier.HostsChangedAsync();
        return true;
    }

    public async Task<bool> DeregisterAsync(Guid hostId, CancellationToken ct)
    {
        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId, ct);
        if (host is null) return false;

        // Clean-up: keep deployment history but detach it from the host; fail jobs that never started.
        var jobs = await _db.DeploymentJobs.Where(j => j.HostId == hostId).ToListAsync(ct);
        foreach (var j in jobs)
        {
            if (j.Status is JobStatus.Pending or JobStatus.Dispatched or JobStatus.Running)
            {
                j.Status = JobStatus.Failed;
                j.Message = "Host was deregistered.";
                j.FinishedAt = DateTime.UtcNow;
            }
            j.HostId = null;
        }
        _db.Hosts.Remove(host); // metric samples are removed by FK cascade
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Host {Hostname} ({HostId}) deregistered", host.Hostname, hostId);
        await _notifier.HostsChangedAsync();
        return true;
    }

    public async Task<List<HostDto>> ListAsync(CancellationToken ct) =>
        (await _db.Hosts.AsNoTracking().OrderBy(h => h.Hostname).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<HostDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var h = await _db.Hosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return h is null ? null : ToDto(h);
    }

    public async Task<List<MetricSampleDto>> MetricsAsync(Guid id, int minutes, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 1440));
        return await _db.Samples.AsNoTracking().Where(s => s.HostId == id && s.At >= since).OrderBy(s => s.At)
            .Select(s => new MetricSampleDto(s.At, s.Cpu, s.MemUsedMb, s.MemTotalMb, s.DiskUsedGb, s.DiskTotalGb))
            .ToListAsync(ct);
    }

    public async Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(CreateEnrollmentTokenRequest req, CancellationToken ct)
    {
        var token = Crypto.RandomToken();
        var expires = DateTime.UtcNow.AddMinutes(req.ValidMinutes);
        _db.EnrollmentTokens.Add(new EnrollmentToken
        {
            Id = Guid.NewGuid(), TokenHash = Crypto.Sha256Hex(token), Note = req.Note, ExpiresAt = expires
        });
        await _db.SaveChangesAsync(ct);
        return new EnrollmentTokenResponse(token, expires);
    }

    private async Task<bool> ConsumeEnrollmentTokenAsync(string token, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_sec.BootstrapEnrollmentToken) && Crypto.FixedTimeEquals(token, _sec.BootstrapEnrollmentToken))
            return true;

        var hash = Crypto.Sha256Hex(token);
        var now = DateTime.UtcNow;
        var t = await _db.EnrollmentTokens.FirstOrDefaultAsync(x => x.TokenHash == hash && x.UsedAt == null && x.ExpiresAt > now, ct);
        if (t is null) return false;
        t.UsedAt = now; // one-time use
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string NormalizeCaps(IEnumerable<string>? caps) =>
        string.Join(",", (caps ?? Enumerable.Empty<string>())
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length is > 0 and <= 32 && c.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
            .Distinct().Take(32));

    public static HostDto ToDto(ManagedHost h) => new(h.Id, h.Hostname, h.IpAddress, h.Os, h.Architecture, h.AgentVersion,
        h.Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries), h.Status.ToString(), h.RegisteredAt,
        h.LastHeartbeatAt, h.CpuPercent, h.MemoryUsedMb, h.MemoryTotalMb, h.DiskUsedGb, h.DiskTotalGb);
}
