using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using MultiHost.Master.Services;

namespace MultiHost.Master.Controllers;

[ApiController, Route("api/enrollment-tokens"), Authorize(Roles = Roles.Admin)]
public class EnrollmentTokensController : ControllerBase
{
    private readonly IHostService _hosts;
    public EnrollmentTokensController(IHostService hosts) => _hosts = hosts;

    /// <summary>Creates a one-time enrollment token. The plaintext token is only returned once.</summary>
    [HttpPost]
    public async Task<EnrollmentTokenResponse> Create(CreateEnrollmentTokenRequest req, CancellationToken ct) =>
        await _hosts.CreateEnrollmentTokenAsync(req, ct);
}

[ApiController, Route("api/hosts"), Authorize(Roles = Roles.Admin)]
public class HostsController : ControllerBase
{
    private readonly IHostService _hosts;
    public HostsController(IHostService hosts) => _hosts = hosts;

    [HttpGet] public async Task<List<HostDto>> List(CancellationToken ct) => await _hosts.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HostDto>> Get(Guid id, CancellationToken ct) =>
        await _hosts.GetAsync(id, ct) is { } h ? h : NotFound();

    [HttpGet("{id:guid}/metrics")]
    public async Task<List<MetricSampleDto>> Metrics(Guid id, [FromQuery] int minutes = 60, CancellationToken ct = default) =>
        await _hosts.MetricsAsync(id, minutes, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deregister(Guid id, CancellationToken ct) =>
        await _hosts.DeregisterAsync(id, ct) ? NoContent() : NotFound();
}

[ApiController, Route("api/applications"), Authorize(Roles = Roles.Admin)]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationService _apps;
    private readonly PlaybookLoader _loader;
    public ApplicationsController(IApplicationService apps, PlaybookLoader loader) { _apps = apps; _loader = loader; }

    [HttpGet] public async Task<List<ApplicationDto>> List(CancellationToken ct) => await _apps.ListAsync(ct);

    [HttpGet("{name}")]
    public async Task<ActionResult<ApplicationDto>> Get(string name, CancellationToken ct) =>
        await _apps.GetAsync(name, ct) is { } a ? a : NotFound();

    /// <summary>Creates or updates an application package (playbooks + variable schema).</summary>
    [HttpPost] public async Task<ApplicationDto> Upsert(PackageUpsertRequest req, CancellationToken ct) => await _apps.UpsertAsync(req, ct);

    /// <summary>Re-reads packages from the playbooks directory.</summary>
    [HttpPost("reload")] public async Task<object> Reload(CancellationToken ct) => new { loaded = await _loader.LoadAsync(ct) };
}

[ApiController, Route("api/deployments"), Authorize(Roles = Roles.Admin)]
public class DeploymentsController : ControllerBase
{
    private readonly IDeploymentService _svc;
    public DeploymentsController(IDeploymentService svc) => _svc = svc;

    [HttpPost]
    public async Task<ActionResult<DeploymentDto>> Create(CreateDeploymentRequest req, CancellationToken ct)
    {
        var d = await _svc.CreateAsync(req, User.UserName(), ct);
        return CreatedAtAction(nameof(Get), new { id = d.Id }, d);
    }

    [HttpGet] public async Task<List<DeploymentDto>> List([FromQuery] int take = 50, CancellationToken ct = default) => await _svc.ListAsync(take, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeploymentDto>> Get(Guid id, CancellationToken ct) =>
        await _svc.GetAsync(id, ct) is { } d ? d : NotFound();

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<ActionResult<JobDetailDto>> Job(Guid jobId, CancellationToken ct) =>
        await _svc.GetJobAsync(jobId, ct) is { } j ? j : NotFound();

    /// <summary>Runs the rollback playbook on every host where this deployment succeeded.</summary>
    [HttpPost("{id:guid}/rollback")]
    public async Task<DeploymentDto> Rollback(Guid id, CancellationToken ct) => await _svc.RollbackAsync(id, User.UserName(), ct);
}
