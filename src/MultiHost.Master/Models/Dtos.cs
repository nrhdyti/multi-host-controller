using System.ComponentModel.DataAnnotations;

namespace MultiHost.Master.Models;

public class LoginRequest
{
    [Required, StringLength(128)] public string Username { get; set; } = "";
    [Required, StringLength(256)] public string Password { get; set; } = "";
}
public record LoginResponse(string Token, DateTime ExpiresAt);

public class CreateEnrollmentTokenRequest
{
    [StringLength(200)] public string? Note { get; set; }
    [Range(1, 10080)] public int ValidMinutes { get; set; } = 60;
}
public record EnrollmentTokenResponse(string Token, DateTime ExpiresAt);

public record HostDto(Guid Id, string Hostname, string IpAddress, string Os, string Architecture, string AgentVersion,
    string[] Capabilities, string Status, DateTime RegisteredAt, DateTime? LastHeartbeatAt, double CpuPercent,
    long MemoryUsedMb, long MemoryTotalMb, long DiskUsedGb, long DiskTotalGb);

public record MetricSampleDto(DateTime At, double Cpu, long MemUsedMb, long MemTotalMb, long DiskUsedGb, long DiskTotalGb);

/// <summary>Describes one input variable of an application package; drives validation and the dashboard form.</summary>
public class VariableDef
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>string | int | port | path | bool | enum</summary>
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public bool Secret { get; set; }
    public string? Pattern { get; set; }
    public List<string>? Options { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
}

public class PackageUpsertRequest
{
    [Required, RegularExpression(@"^[a-z0-9][a-z0-9-]{0,63}$")] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    [Required, StringLength(32)] public string Version { get; set; } = "1.0.0";
    [Required, StringLength(200_000)] public string InstallPlaybook { get; set; } = "";
    [StringLength(200_000)] public string? VerifyPlaybook { get; set; }
    [StringLength(200_000)] public string? RollbackPlaybook { get; set; }
    public List<VariableDef> Variables { get; set; } = new();
    public List<string> RequiredCapabilities { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
}

public record ApplicationDto(string Name, string Description, string Version, string[] RequiredCapabilities,
    string[] Dependencies, List<VariableDef> Variables, bool HasVerify, bool HasRollback, DateTime UpdatedAt);

public class CreateDeploymentRequest
{
    [Required] public string ApplicationName { get; set; } = "";
    [Required, MinLength(1), MaxLength(100)] public List<Guid> HostIds { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();
}

public record JobDto(Guid Id, Guid? HostId, string Hostname, string Action, string Status, string? Message,
    bool Verified, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);
public record JobDetailDto(JobDto Job, string Log, Dictionary<string, string> Variables);
public record DeploymentDto(Guid Id, string ApplicationName, string ApplicationVersion, string Kind, string Status,
    DateTime CreatedAt, string CreatedBy, List<JobDto> Jobs);
