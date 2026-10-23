using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using MultiHost.Agent.Models;
using MultiHost.Shared;

namespace MultiHost.Agent.Services;

/// <summary>Registers with the master, then runs a heartbeat loop and a job-polling loop concurrently.</summary>
public class AgentWorker : BackgroundService
{
    private readonly AgentOptions _opt;
    private readonly MasterClient _master;
    private readonly AgentStateStore _state;
    private readonly MetricsCollector _metrics;
    private readonly JobExecutor _executor;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentWorker> _log;
    private int _heartbeatSeconds = 15;
    private bool _revoked;

    public AgentWorker(IOptions<AgentOptions> opt, MasterClient master, AgentStateStore state, MetricsCollector metrics,
        JobExecutor executor, IHostApplicationLifetime lifetime, ILogger<AgentWorker> log)
    {
        _opt = opt.Value; _master = master; _state = state; _metrics = metrics; _executor = executor; _lifetime = lifetime; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var uri = new Uri(_opt.MasterUrl);
        if (uri.Scheme != Uri.UriSchemeHttps && !_opt.AllowInsecureHttp)
        {
            _log.LogCritical("Refusing to talk to {Url} over plain HTTP. Use HTTPS or set Agent:AllowInsecureHttp=true (demo only).", _opt.MasterUrl);
            _lifetime.StopApplication();
            return;
        }

        try { await EnsureRegisteredAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try { await Task.WhenAll(HeartbeatLoopAsync(linked), JobLoopAsync(linked)); }
        catch (OperationCanceledException) { /* shutting down or revoked */ }

        if (_revoked)
        {
            _log.LogWarning("This agent was deregistered (or its token revoked). Clearing local state and exiting.");
            _state.Clear();
            _lifetime.StopApplication();
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        var saved = _state.Load();
        if (saved is not null)
        {
            _master.Token = saved.Token;
            _log.LogInformation("Resuming as host {HostId}", saved.HostId);
            return;
        }

        var delay = 2;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(_opt.EnrollmentToken)) throw new InvalidOperationException("Agent:EnrollmentToken is not configured.");
                var res = await _master.RegisterAsync(new RegisterAgentRequest
                {
                    EnrollmentToken = _opt.EnrollmentToken,
                    Hostname = string.IsNullOrWhiteSpace(_opt.Hostname) ? Environment.MachineName : _opt.Hostname,
                    IpAddress = MetricsCollector.GetIp(),
                    Os = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture.ToString(),
                    AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
                    Capabilities = MetricsCollector.DetectCapabilities(_opt.AnsiblePlaybookPath)
                }, ct);
                _master.Token = res.Token;
                _heartbeatSeconds = res.HeartbeatIntervalSeconds;
                _state.Save(new AgentState { HostId = res.HostId, Token = res.Token });
                _log.LogInformation("Registered with master as {HostId}", res.HostId);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Registration failed: {Message}. Retrying in {Delay}s", ex.Message, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                delay = Math.Min(delay * 2, 60);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationTokenSource linked)
    {
        var ct = linked.Token;
        var caps = MetricsCollector.DetectCapabilities(_opt.AnsiblePlaybookPath);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hb = _metrics.Collect();
                hb.Capabilities = caps;
                var res = await _master.HeartbeatAsync(hb, ct);
                _heartbeatSeconds = Math.Max(2, res.HeartbeatIntervalSeconds);
            }
            catch (AgentRevokedException) { _revoked = true; linked.Cancel(); return; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Heartbeat failed: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), ct);
        }
    }

    private async Task JobLoopAsync(CancellationTokenSource linked)
    {
        var ct = linked.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await _master.NextJobAsync(ct);
                if (job is not null) { await _executor.RunAsync(job, ct); continue; }
            }
            catch (AgentRevokedException) { _revoked = true; linked.Cancel(); return; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Job poll failed: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.JobPollSeconds)), ct);
        }
    }
}
