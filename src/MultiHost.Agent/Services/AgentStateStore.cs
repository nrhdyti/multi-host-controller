using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiHost.Agent.Models;

namespace MultiHost.Agent.Services;

/// <summary>Persists the host id and agent token so restarts do not need a new enrollment token.</summary>
public class AgentStateStore
{
    private readonly string _path;
    public AgentStateStore(IOptions<AgentOptions> opt) => _path = Path.GetFullPath(opt.Value.StateFile);

    public AgentState? Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_path)) : null; }
        catch { return null; }
    }

    public void Save(AgentState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(s));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear() { try { File.Delete(_path); } catch { /* best effort */ } }
}
