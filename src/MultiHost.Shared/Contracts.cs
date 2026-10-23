using System.ComponentModel.DataAnnotations;

namespace MultiHost.Shared;

/// <summary>Lifecycle of a per-host deployment job.</summary>
public enum JobStatus { Pending, Dispatched, Running, Succeeded, Failed, RolledBack, RollbackFailed }

public static class JobActions
{
    public const string Deploy = "deploy";
    public const string Rollback = "rollback";
}

public class RegisterAgentRequest
{
    [Required, StringLength(512)] public string EnrollmentToken { get; set; } = "";
    [Required, RegularExpression(@"^[A-Za-z0-9._-]{1,128}$", ErrorMessage = "Invalid hostname.")]
    public string Hostname { get; set; } = "";
    [StringLength(64)] public string IpAddress { get; set; } = "";
    [StringLength(256)] public string Os { get; set; } = "";
    [StringLength(64)] public string Architecture { get; set; } = "";
    [StringLength(32)] public string AgentVersion { get; set; } = "";
    [MaxLength(32)] public List<string> Capabilities { get; set; } = new();
}

public class RegisterAgentResponse
{
    public Guid HostId { get; set; }
    public string Token { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; }
}

public class HeartbeatRequest
{
    [Range(0, 100)] public double CpuPercent { get; set; }
    [Range(0, 1e12)] public long MemoryUsedMb { get; set; }
    [Range(0, 1e12)] public long MemoryTotalMb { get; set; }
    [Range(0, 1e12)] public long DiskUsedGb { get; set; }
    [Range(0, 1e12)] public long DiskTotalGb { get; set; }
    [StringLength(64)] public string? IpAddress { get; set; }
    [MaxLength(32)] public List<string>? Capabilities { get; set; }
}

public class HeartbeatResponse
{
    public DateTime ServerTimeUtc { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }
}

/// <summary>Work item handed to an agent. All playbooks are plain YAML text.</summary>
public class AgentJob
{
    public Guid JobId { get; set; }
    public string Action { get; set; } = JobActions.Deploy;
    public string ApplicationName { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public string InstallPlaybook { get; set; } = "";
    public string? VerifyPlaybook { get; set; }
    public string? RollbackPlaybook { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new();
    /// <summary>Names of variables whose values must be redacted from logs.</summary>
    public List<string> SecretVariables { get; set; } = new();
}

public class JobLogChunk
{
    [Required, StringLength(200_000)] public string Text { get; set; } = "";
}

public class JobResultRequest
{
    /// <summary>One of: Succeeded, Failed, RolledBack, RollbackFailed.</summary>
    [Required] public string Status { get; set; } = "";
    [StringLength(2000)] public string? Message { get; set; }
    public bool Verified { get; set; }
}
