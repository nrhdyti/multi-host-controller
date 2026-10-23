using System.Diagnostics;
using Microsoft.Extensions.Options;
using MultiHost.Agent.Models;

namespace MultiHost.Agent.Services;

/// <summary>Runs ansible-playbook locally. Arguments are passed as an array (no shell), variables via a JSON file.</summary>
public class AnsibleRunner
{
    private readonly AgentOptions _opt;
    private readonly ILogger<AnsibleRunner> _log;
    public AnsibleRunner(IOptions<AgentOptions> opt, ILogger<AnsibleRunner> log) { _opt = opt.Value; _log = log; }

    public async Task<int> RunAsync(string workDir, string playbookFile, string varsFile, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_opt.AnsiblePlaybookPath)
        {
            WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in new[] { "-i", "localhost,", "-c", "local", playbookFile, "-e", "@" + varsFile }) psi.ArgumentList.Add(a);
        psi.Environment["ANSIBLE_NOCOLOR"] = "1";
        psi.Environment["ANSIBLE_FORCE_COLOR"] = "0";
        psi.Environment["ANSIBLE_HOST_KEY_CHECKING"] = "False";
        psi.Environment["ANSIBLE_RETRY_FILES_ENABLED"] = "0";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };

        _log.LogInformation("Running {Playbook} in {Dir}", playbookFile, workDir);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(_opt.JobTimeoutMinutes));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
            onLine("ERROR: playbook timed out or the agent is shutting down; process killed.");
            return -1;
        }
        p.WaitForExit(); // ensure async output handlers have drained
        return p.ExitCode;
    }
}
