using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiHost.Agent.Models;
using MultiHost.Shared;

namespace MultiHost.Agent.Services;

/// <summary>
/// Executes one job: install -> verify, and on any failure runs the rollback playbook.
/// Reports streamed logs and a final status to the master.
/// </summary>
public class JobExecutor
{
    private readonly AgentOptions _opt;
    private readonly AnsibleRunner _ansible;
    private readonly MasterClient _master;
    private readonly ILogger<JobExecutor> _log;

    public JobExecutor(IOptions<AgentOptions> opt, AnsibleRunner ansible, MasterClient master, ILogger<JobExecutor> log)
    { _opt = opt.Value; _ansible = ansible; _master = master; _log = log; }

    public async Task RunAsync(AgentJob job, CancellationToken ct)
    {
        _log.LogInformation("Starting job {Job}: {Action} {App} {Version}", job.JobId, job.Action, job.ApplicationName, job.ApplicationVersion);
        var root = string.IsNullOrWhiteSpace(_opt.WorkDirectory) ? Path.Combine(Path.GetTempPath(), "multihost-agent") : _opt.WorkDirectory;
        var dir = Path.Combine(root, job.JobId.ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var secrets = job.SecretVariables.Where(job.Variables.ContainsKey).Select(k => job.Variables[k]).Where(v => v.Length >= 4).ToList();
        await using var logs = new LogBuffer(t => _master.AppendLogAsync(job.JobId, t, CancellationToken.None), secrets, _log);

        string status; string message; bool verified = false;
        try
        {
            var varsPath = Path.Combine(dir, "vars.json");
            await WriteSecureAsync(varsPath, JsonSerializer.Serialize(job.Variables), ct);
            await WriteSecureAsync(Path.Combine(dir, "install.yml"), job.InstallPlaybook, ct);
            if (!string.IsNullOrWhiteSpace(job.VerifyPlaybook)) await WriteSecureAsync(Path.Combine(dir, "verify.yml"), job.VerifyPlaybook, ct);
            var hasRollback = !string.IsNullOrWhiteSpace(job.RollbackPlaybook);
            if (hasRollback) await WriteSecureAsync(Path.Combine(dir, "rollback.yml"), job.RollbackPlaybook!, ct);

            Task<int> Run(string file) => _ansible.RunAsync(dir, file, varsPath, logs.Add, ct);

            if (job.Action == JobActions.Rollback)
            {
                if (!hasRollback) { status = nameof(JobStatus.RollbackFailed); message = "No rollback playbook."; }
                else
                {
                    logs.Add("=== Running rollback playbook ===");
                    var ok = await Run("rollback.yml") == 0;
                    status = ok ? nameof(JobStatus.RolledBack) : nameof(JobStatus.RollbackFailed);
                    message = ok ? "Rollback completed." : "Rollback playbook failed.";
                }
            }
            else
            {
                logs.Add("=== Running install playbook ===");
                var ok = await Run("install.yml") == 0;
                string failure = "Install playbook failed.";
                if (ok && !string.IsNullOrWhiteSpace(job.VerifyPlaybook))
                {
                    logs.Add("=== Running verification playbook ===");
                    ok = await Run("verify.yml") == 0;
                    verified = ok;
                    if (!ok) failure = "Post-install verification failed.";
                }

                if (ok) { status = nameof(JobStatus.Succeeded); message = verified ? "Installed and verified." : "Installed."; }
                else if (hasRollback)
                {
                    logs.Add($"=== {failure} Running rollback playbook ===");
                    var rolled = await Run("rollback.yml") == 0;
                    status = rolled ? nameof(JobStatus.RolledBack) : nameof(JobStatus.RollbackFailed);
                    message = rolled ? $"{failure} Changes were rolled back." : $"{failure} Rollback also failed; manual cleanup required.";
                }
                else { status = nameof(JobStatus.Failed); message = failure; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Job {Job} crashed", job.JobId);
            logs.Add($"ERROR: {ex.Message}");
            status = nameof(JobStatus.Failed);
            message = "Agent error: " + ex.Message;
        }
        finally
        {
            await logs.FlushAsync();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        await _master.CompleteAsync(job.JobId, new JobResultRequest { Status = status, Message = message, Verified = verified }, CancellationToken.None);
        _log.LogInformation("Job {Job} finished with {Status}", job.JobId, status);
    }

    private static async Task WriteSecureAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Batches output lines, redacts secret values and ships them to the master every 2 seconds.</summary>
    private sealed class LogBuffer : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _q = new();
        private readonly Func<string, Task> _send;
        private readonly List<string> _secrets;
        private readonly ILogger _log;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public LogBuffer(Func<string, Task> send, List<string> secrets, ILogger log)
        {
            _send = send; _secrets = secrets; _log = log;
            _loop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                try { while (await timer.WaitForNextTickAsync(_cts.Token)) await FlushAsync(); }
                catch (OperationCanceledException) { }
            });
        }

        public void Add(string line)
        {
            foreach (var s in _secrets) line = line.Replace(s, "********");
            _q.Enqueue(line);
        }

        public async Task FlushAsync()
        {
            var sb = new StringBuilder();
            while (_q.TryDequeue(out var l)) sb.AppendLine(l);
            if (sb.Length == 0) return;
            try { await _send(sb.ToString()); }
            catch (Exception ex) { _log.LogWarning("Could not ship job log to master: {Message}", ex.Message); }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
            await FlushAsync();
            _cts.Dispose();
        }
    }
}
