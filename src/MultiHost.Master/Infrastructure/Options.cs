namespace MultiHost.Master.Infrastructure;

public class SecurityOptions
{
    public string JwtKey { get; set; } = "";
    public string Issuer { get; set; } = "multihost-master";
    public int AdminTokenMinutes { get; set; } = 60;
    public int AgentTokenDays { get; set; } = 365;
    /// <summary>Optional reusable enrollment secret (handy for demos / Docker Compose). Leave empty in production.</summary>
    public string? BootstrapEnrollmentToken { get; set; }
}

public class AdminOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int OfflineAfterSeconds { get; set; } = 45;
    public int CheckIntervalSeconds { get; set; } = 10;
    public int JobTimeoutMinutes { get; set; } = 30;
    public int MetricRetentionHours { get; set; } = 24;
}

public class PlaybookOptions { public string Directory { get; set; } = "playbooks"; }

public static class Roles
{
    public const string Admin = "Admin";
    public const string Agent = "Agent";
}
