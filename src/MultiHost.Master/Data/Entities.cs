using MultiHost.Shared;

namespace MultiHost.Master.Data;

public enum HostStatus { Online, Offline }

public class ManagedHost
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    /// <summary>Comma separated capability tags, e.g. "linux,ansible,root".</summary>
    public string Capabilities { get; set; } = "";
    public HostStatus Status { get; set; } = HostStatus.Offline;
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryUsedMb { get; set; }
    public long MemoryTotalMb { get; set; }
    public long DiskUsedGb { get; set; }
    public long DiskTotalGb { get; set; }
    public List<MetricSample> Samples { get; set; } = new();
}

public class MetricSample
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public DateTime At { get; set; }
    public double Cpu { get; set; }
    public long MemUsedMb { get; set; }
    public long MemTotalMb { get; set; }
    public long DiskUsedGb { get; set; }
    public long DiskTotalGb { get; set; }
}

public class EnrollmentToken
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string? Note { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class ApplicationPackage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string InstallPlaybook { get; set; } = "";
    public string? VerifyPlaybook { get; set; }
    public string? RollbackPlaybook { get; set; }
    /// <summary>JSON array of VariableDef.</summary>
    public string VariablesJson { get; set; } = "[]";
    public string RequiredCapabilities { get; set; } = "";
    public string Dependencies { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class Deployment
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public ApplicationPackage Application { get; set; } = null!;
    public string ApplicationName { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    /// <summary>"deploy" or "rollback".</summary>
    public string Kind { get; set; } = JobActions.Deploy;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<DeploymentJob> Jobs { get; set; } = new();
}

public class DeploymentJob
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public Guid? HostId { get; set; }
    public string Hostname { get; set; } = "";
    public string Action { get; set; } = JobActions.Deploy;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    /// <summary>Resolved variables (JSON). Secret values are masked in API responses.</summary>
    public string VariablesJson { get; set; } = "{}";
    public string Log { get; set; } = "";
    public string? Message { get; set; }
    public bool Verified { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? LastUpdateAt { get; set; }
}
