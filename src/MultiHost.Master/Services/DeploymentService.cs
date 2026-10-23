using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using MultiHost.Shared;

namespace MultiHost.Master.Services;

public interface IDeploymentService
{
    Task<DeploymentDto> CreateAsync(CreateDeploymentRequest req, string user, CancellationToken ct);
    Task<DeploymentDto> RollbackAsync(Guid deploymentId, string user, CancellationToken ct);
    Task<List<DeploymentDto>> ListAsync(int take, CancellationToken ct);
    Task<DeploymentDto?> GetAsync(Guid id, CancellationToken ct);
    Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken ct);
    // Agent-facing
    Task<AgentJob?> GetNextJobAsync(Guid hostId, CancellationToken ct);
    Task<bool> AppendLogAsync(Guid hostId, Guid jobId, string text, CancellationToken ct);
    Task<bool> CompleteAsync(Guid hostId, Guid jobId, JobResultRequest result, CancellationToken ct);
}

public class DeploymentService : IDeploymentService
{
    private const int MaxLogChars = 1_000_000;
    // Single-master assumption: serialises job hand-out. For multiple masters use row locks (FOR UPDATE SKIP LOCKED).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly MasterDbContext _db;
    private readonly IStatusNotifier _notifier;
    private readonly ILogger<DeploymentService> _log;

    public DeploymentService(MasterDbContext db, IStatusNotifier notifier, ILogger<DeploymentService> log)
    { _db = db; _notifier = notifier; _log = log; }

    public async Task<DeploymentDto> CreateAsync(CreateDeploymentRequest req, string user, CancellationToken ct)
    {
        var app = await _db.Applications.FirstOrDefaultAsync(a => a.Name == req.ApplicationName, ct)
                  ?? throw new ApiException(404, $"Application '{req.ApplicationName}' not found.");
        var ids = req.HostIds.Distinct().ToList();
        var hosts = await _db.Hosts.Where(h => ids.Contains(h.Id)).ToListAsync(ct);
        if (hosts.Count != ids.Count) throw new ApiException(400, "One or more hosts do not exist.");

        var defs = ApplicationService.ParseVariables(app.VariablesJson);
        var vars = VariableValidator.Resolve(defs, req.Variables);

        var required = app.RequiredCapabilities.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deps = app.Dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var problems = new List<string>();
        foreach (var h in hosts)
        {
            if (h.Status != HostStatus.Online) { problems.Add($"{h.Hostname}: host is offline."); continue; }
            var caps = h.Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var missing = required.Except(caps).ToList();
            if (missing.Count > 0) problems.Add($"{h.Hostname}: missing prerequisites [{string.Join(", ", missing)}].");
            foreach (var dep in deps)
                if (!await DependencySatisfiedAsync(h.Id, dep, ct)) problems.Add($"{h.Hostname}: dependency '{dep}' is not deployed.");
        }
        if (problems.Count > 0) throw new ApiException(422, string.Join(" ", problems));

        var now = DateTime.UtcNow;
        var varsJson = JsonSerializer.Serialize(vars);
        var dep0 = new Deployment
        {
            Id = Guid.NewGuid(), ApplicationId = app.Id, ApplicationName = app.Name, ApplicationVersion = app.Version,
            Kind = JobActions.Deploy, CreatedAt = now, CreatedBy = user,
            Jobs = hosts.Select(h => new DeploymentJob
            {
                Id = Guid.NewGuid(), HostId = h.Id, Hostname = h.Hostname, Action = JobActions.Deploy,
                Status = JobStatus.Pending, VariablesJson = varsJson, CreatedAt = now
            }).ToList()
        };
        _db.Deployments.Add(dep0);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Deployment {Id} of {App} created by {User} for {Count} host(s)", dep0.Id, app.Name, user, hosts.Count);
        await _notifier.DeploymentChangedAsync(dep0.Id);
        return ToDto(dep0);
    }

    public async Task<DeploymentDto> RollbackAsync(Guid deploymentId, string user, CancellationToken ct)
    {
        var orig = await _db.Deployments.Include(d => d.Jobs).Include(d => d.Application)
                       .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
                   ?? throw new ApiException(404, "Deployment not found.");
        if (string.IsNullOrWhiteSpace(orig.Application.RollbackPlaybook))
            throw new ApiException(422, "This application has no rollback playbook.");

        var hostIds = orig.Jobs.Where(j => j.HostId != null).Select(j => j.HostId!.Value).ToList();
        var online = await _db.Hosts.Where(h => hostIds.Contains(h.Id) && h.Status == HostStatus.Online).Select(h => h.Id).ToListAsync(ct);
        var eligible = orig.Jobs.Where(j => j.Action == JobActions.Deploy && j.Status == JobStatus.Succeeded
                                            && j.HostId != null && online.Contains(j.HostId.Value)).ToList();
        if (eligible.Count == 0) throw new ApiException(422, "No successfully deployed, online hosts to roll back.");

        var now = DateTime.UtcNow;
        var d = new Deployment
        {
            Id = Guid.NewGuid(), ApplicationId = orig.ApplicationId, ApplicationName = orig.ApplicationName,
            ApplicationVersion = orig.ApplicationVersion, Kind = JobActions.Rollback, CreatedAt = now, CreatedBy = user,
            Jobs = eligible.Select(j => new DeploymentJob
            {
                Id = Guid.NewGuid(), HostId = j.HostId, Hostname = j.Hostname, Action = JobActions.Rollback,
                Status = JobStatus.Pending, VariablesJson = j.VariablesJson, CreatedAt = now
            }).ToList()
        };
        _db.Deployments.Add(d);
        await _db.SaveChangesAsync(ct);
        await _notifier.DeploymentChangedAsync(d.Id);
        return ToDto(d);
    }

    public async Task<List<DeploymentDto>> ListAsync(int take, CancellationToken ct) =>
        (await _db.Deployments.AsNoTracking().Include(d => d.Jobs).OrderByDescending(d => d.CreatedAt)
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<DeploymentDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var d = await _db.Deployments.AsNoTracking().Include(x => x.Jobs).FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null ? null : ToDto(d);
    }

    public async Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken ct)
    {
        var j = await _db.DeploymentJobs.AsNoTracking().Include(x => x.Deployment).ThenInclude(d => d.Application)
            .FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (j is null) return null;
        var defs = ApplicationService.ParseVariables(j.Deployment.Application.VariablesJson);
        var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(j.VariablesJson) ?? new();
        foreach (var d in defs.Where(d => d.Secret && vars.ContainsKey(d.Name))) vars[d.Name] = "********";
        return new JobDetailDto(ToJobDto(j), j.Log, vars);
    }

    // ---------------- agent-facing ----------------

    public async Task<AgentJob?> GetNextJobAsync(Guid hostId, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var busy = await _db.DeploymentJobs.AnyAsync(j => j.HostId == hostId &&
                (j.Status == JobStatus.Dispatched || j.Status == JobStatus.Running), ct);
            if (busy) return null;

            var job = await _db.DeploymentJobs.Include(j => j.Deployment).ThenInclude(d => d.Application)
                .Where(j => j.HostId == hostId && j.Status == JobStatus.Pending)
                .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            if (job is null) return null;

            job.Status = JobStatus.Dispatched;
            job.StartedAt = job.LastUpdateAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _notifier.DeploymentChangedAsync(job.DeploymentId);

            var app = job.Deployment.Application;
            return new AgentJob
            {
                JobId = job.Id, Action = job.Action, ApplicationName = app.Name, ApplicationVersion = app.Version,
                InstallPlaybook = app.InstallPlaybook, VerifyPlaybook = app.VerifyPlaybook, RollbackPlaybook = app.RollbackPlaybook,
                Variables = JsonSerializer.Deserialize<Dictionary<string, string>>(job.VariablesJson) ?? new(),
                SecretVariables = ApplicationService.ParseVariables(app.VariablesJson).Where(v => v.Secret).Select(v => v.Name).ToList()
            };
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> AppendLogAsync(Guid hostId, Guid jobId, string text, CancellationToken ct)
    {
        var job = await _db.DeploymentJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.HostId == hostId, ct);
        if (job is null || job.Status is not (JobStatus.Dispatched or JobStatus.Running)) return false;
        job.Status = JobStatus.Running;
        job.LastUpdateAt = DateTime.UtcNow;
        if (job.Log.Length < MaxLogChars) job.Log += text;
        await _db.SaveChangesAsync(ct);
        await _notifier.DeploymentChangedAsync(job.DeploymentId);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid hostId, Guid jobId, JobResultRequest result, CancellationToken ct)
    {
        if (!Enum.TryParse<JobStatus>(result.Status, true, out var status) ||
            status is not (JobStatus.Succeeded or JobStatus.Failed or JobStatus.RolledBack or JobStatus.RollbackFailed))
            throw new ApiException(400, "Status must be Succeeded, Failed, RolledBack or RollbackFailed.");

        var job = await _db.DeploymentJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.HostId == hostId, ct);
        if (job is null || job.Status is not (JobStatus.Dispatched or JobStatus.Running)) return false;
        job.Status = status;
        job.Message = result.Message;
        job.Verified = result.Verified;
        job.FinishedAt = job.LastUpdateAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Job {JobId} on {Host} finished: {Status}", job.Id, job.Hostname, status);
        await _notifier.DeploymentChangedAsync(job.DeploymentId);
        return true;
    }

    // ---------------- helpers ----------------

    private async Task<bool> DependencySatisfiedAsync(Guid hostId, string appName, CancellationToken ct)
    {
        var latest = await _db.DeploymentJobs.AsNoTracking()
            .Where(j => j.HostId == hostId && j.Deployment.ApplicationName == appName)
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        return latest is { Action: JobActions.Deploy, Status: JobStatus.Succeeded };
    }

    private static JobDto ToJobDto(DeploymentJob j) => new(j.Id, j.HostId, j.Hostname, j.Action, j.Status.ToString(),
        j.Message, j.Verified, j.CreatedAt, j.StartedAt, j.FinishedAt);

    public static DeploymentDto ToDto(Deployment d) => new(d.Id, d.ApplicationName, d.ApplicationVersion, d.Kind,
        Aggregate(d), d.CreatedAt, d.CreatedBy, d.Jobs.OrderBy(j => j.Hostname).Select(ToJobDto).ToList());

    private static string Aggregate(Deployment d)
    {
        if (d.Jobs.Any(j => j.Status is JobStatus.Pending or JobStatus.Dispatched or JobStatus.Running)) return "InProgress";
        var ok = d.Kind == JobActions.Rollback
            ? d.Jobs.All(j => j.Status == JobStatus.RolledBack)
            : d.Jobs.All(j => j.Status == JobStatus.Succeeded);
        return ok ? "Succeeded" : "Failed";
    }
}
