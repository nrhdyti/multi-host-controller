using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Services;
using MultiHost.Shared;

namespace MultiHost.Master.Controllers;

/// <summary>Endpoints called by agents. Registration is anonymous (enrollment token); everything else needs the agent JWT.</summary>
[ApiController, Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly IHostService _hosts;
    private readonly IDeploymentService _deployments;
    private readonly Microsoft.Extensions.Options.IOptions<MonitoringOptions> _mon;
    public AgentController(IHostService hosts, IDeploymentService deployments, Microsoft.Extensions.Options.IOptions<MonitoringOptions> mon)
    { _hosts = hosts; _deployments = deployments; _mon = mon; }

    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<RegisterAgentResponse>> Register(RegisterAgentRequest req, CancellationToken ct)
    {
        var res = await _hosts.RegisterAsync(req, ct);
        return res is null ? Unauthorized(new ProblemDetails { Title = "Invalid or expired enrollment token" }) : Ok(res);
    }

    [HttpPost("heartbeat"), Authorize(Roles = Roles.Agent)]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat(HeartbeatRequest hb, CancellationToken ct)
    {
        if (!await _hosts.HeartbeatAsync(User.HostId(), hb, ct)) return Unauthorized();
        return new HeartbeatResponse { ServerTimeUtc = DateTime.UtcNow, HeartbeatIntervalSeconds = _mon.Value.HeartbeatIntervalSeconds };
    }

    [HttpGet("jobs/next"), Authorize(Roles = Roles.Agent)]
    public async Task<ActionResult<AgentJob>> NextJob(CancellationToken ct)
    {
        var job = await _deployments.GetNextJobAsync(User.HostId(), ct);
        return job is null ? NoContent() : Ok(job);
    }

    [HttpPost("jobs/{jobId:guid}/log"), Authorize(Roles = Roles.Agent)]
    public async Task<IActionResult> AppendLog(Guid jobId, JobLogChunk chunk, CancellationToken ct) =>
        await _deployments.AppendLogAsync(User.HostId(), jobId, chunk.Text, ct) ? NoContent() : NotFound();

    [HttpPost("jobs/{jobId:guid}/result"), Authorize(Roles = Roles.Agent)]
    public async Task<IActionResult> Complete(Guid jobId, JobResultRequest result, CancellationToken ct) =>
        await _deployments.CompleteAsync(User.HostId(), jobId, result, ct) ? NoContent() : NotFound();

    /// <summary>Agent-initiated deregistration.</summary>
    [HttpDelete("self"), Authorize(Roles = Roles.Agent)]
    public async Task<IActionResult> Deregister(CancellationToken ct) =>
        await _hosts.DeregisterAsync(User.HostId(), ct) ? NoContent() : NotFound();
}
