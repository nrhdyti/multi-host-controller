namespace MultiHost.Agent.Models;

public class AgentOptions
{
    public string MasterUrl { get; set; } = "";
    /// <summary>Only needed for first registration (or when state is lost).</summary>
    public string EnrollmentToken { get; set; } = "";
    /// <summary>Plain HTTP is refused unless explicitly allowed (local demos only).</summary>
    public bool AllowInsecureHttp { get; set; }
    public bool AllowInvalidCertificate { get; set; }
    public string StateFile { get; set; } = "agent-state.json";
    public string LogDirectory { get; set; } = "logs";
    public string WorkDirectory { get; set; } = "";
    public int JobPollSeconds { get; set; } = 3;
    public int JobTimeoutMinutes { get; set; } = 30;
    public string AnsiblePlaybookPath { get; set; } = "ansible-playbook";
    public string Hostname { get; set; } = "";
}

public class AgentState
{
    public Guid HostId { get; set; }
    public string Token { get; set; } = "";
}

/// <summary>Raised when the master no longer accepts this agent's token (host deregistered or token revoked).</summary>
public class AgentRevokedException : Exception
{
    public AgentRevokedException() : base("The master rejected this agent's credentials.") { }
}
