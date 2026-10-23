using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiHost.Master.Data;
using MultiHost.Shared;
using Xunit;
using Xunit.Sdk;

namespace MultiHost.Master.Tests;

public class MasterFactory : WebApplicationFactory<Program>
{
    private static string FindPlaybooks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "playbooks", "minio"))) dir = dir.Parent;
        return dir is null ? "playbooks" : Path.Combine(dir.FullName, "playbooks");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Admin:Password", "test-password-123");
        builder.UseSetting("Security:BootstrapEnrollmentToken", "bootstrap-token-for-tests");
        builder.UseSetting("Playbooks:Directory", FindPlaybooks());
        builder.UseSetting("Monitoring:Enabled", "false");
        builder.ConfigureServices(s =>
        {
            s.Remove(s.Single(d => d.ServiceType == typeof(DbContextOptions<MasterDbContext>)));
            var name = "tests-" + Guid.NewGuid();
            s.AddDbContext<MasterDbContext>(o => o.UseInMemoryDatabase(name));
        });
    }
}

public class ApiIntegrationTests : IClassFixture<MasterFactory>
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    private readonly MasterFactory _factory;
    public ApiIntegrationTests(MasterFactory factory) => _factory = factory;

    private static async Task EnsureOkAsync(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) throw new XunitException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    private static async Task ExpectAsync(HttpStatusCode expected, HttpResponseMessage r)
    {
        if (r.StatusCode != expected) throw new XunitException($"Expected {expected} but got {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-123" });
        await EnsureOkAsync(r);
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<(HttpClient Client, Guid HostId)> RegisterAgentAsync(string hostname, params string[] caps)
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/agent/register", new RegisterAgentRequest
        {
            EnrollmentToken = "bootstrap-token-for-tests", Hostname = hostname, IpAddress = "10.0.0.5", Os = "Linux",
            Architecture = "X64", AgentVersion = "1.0.0", Capabilities = caps.ToList()
        });
        await EnsureOkAsync(r);
        var res = (await r.Content.ReadFromJsonAsync<RegisterAgentResponse>(J))!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.Token);
        return (c, res.HostId);
    }

    [Fact]
    public async Task AdminEndpointsRequireAuthentication()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/hosts")).StatusCode);
    }

    [Fact]
    public async Task LoginRejectsBadPassword()
    {
        var r = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope" });
        await ExpectAsync(HttpStatusCode.Unauthorized, r);
    }

    [Fact]
    public async Task RegistrationRejectsInvalidEnrollmentToken()
    {
        var r = await _factory.CreateClient().PostAsJsonAsync("/api/agent/register",
            new RegisterAgentRequest { EnrollmentToken = "wrong", Hostname = "evil" });
        await ExpectAsync(HttpStatusCode.Unauthorized, r);
    }

    [Fact]
    public async Task AgentTokenCannotCallAdminApi()
    {
        var (agent, _) = await RegisterAgentAsync("role-check");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/hosts")).StatusCode);
    }

    [Fact]
    public async Task OneTimeEnrollmentTokenWorksOnce()
    {
        var admin = await AdminClientAsync();
        var tr = await admin.PostAsJsonAsync("/api/enrollment-tokens", new { validMinutes = 5 });
        var token = (await tr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var anon = _factory.CreateClient();
        var first = await anon.PostAsJsonAsync("/api/agent/register", new RegisterAgentRequest { EnrollmentToken = token, Hostname = "once-a" });
        var second = await anon.PostAsJsonAsync("/api/agent/register", new RegisterAgentRequest { EnrollmentToken = token, Hostname = "once-b" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task HeartbeatUpdatesHostAndDeregistrationRevokesToken()
    {
        var admin = await AdminClientAsync();
        var (agent, id) = await RegisterAgentAsync("hb-host", "linux");

        var hb = await agent.PostAsJsonAsync("/api/agent/heartbeat",
            new HeartbeatRequest { CpuPercent = 42.5, MemoryUsedMb = 512, MemoryTotalMb = 2048, DiskUsedGb = 5, DiskTotalGb = 50 });
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);

        var host = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{id}");
        Assert.Equal("Online", host.GetProperty("status").GetString());
        Assert.Equal(42.5, host.GetProperty("cpuPercent").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/hosts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/agent/heartbeat", new HeartbeatRequest())).StatusCode);
    }

    [Fact]
    public async Task FullDeploymentLifecycle()
    {
        var admin = await AdminClientAsync();
        var (agent, hostId) = await RegisterAgentAsync("deploy-host", "linux", "ansible", "root");

        var create = await admin.PostAsJsonAsync("/api/deployments", new
        {
            applicationName = "minio", hostIds = new[] { hostId },
            variables = new Dictionary<string, string> { ["minio_root_password"] = "SuperSecret123" }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dep = await create.Content.ReadFromJsonAsync<JsonElement>();
        var depId = dep.GetProperty("id").GetGuid();
        Assert.Equal("InProgress", dep.GetProperty("status").GetString());

        var next = await agent.GetAsync("/api/agent/jobs/next");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var job = (await next.Content.ReadFromJsonAsync<AgentJob>(J))!;
        Assert.Equal("minio", job.ApplicationName);
        Assert.Equal("SuperSecret123", job.Variables["minio_root_password"]);
        Assert.Contains("minio_root_password", job.SecretVariables);
        Assert.False(string.IsNullOrWhiteSpace(job.RollbackPlaybook));
        Assert.Equal(HttpStatusCode.NoContent, (await agent.GetAsync("/api/agent/jobs/next")).StatusCode); // already handed out

        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync($"/api/agent/jobs/{job.JobId}/log", new JobLogChunk { Text = "PLAY [Install]\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync($"/api/agent/jobs/{job.JobId}/result",
            new JobResultRequest { Status = "Succeeded", Verified = true, Message = "ok" })).StatusCode);

        var done = await admin.GetFromJsonAsync<JsonElement>($"/api/deployments/{depId}");
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/deployments/jobs/{job.JobId}");
        Assert.Contains("PLAY [Install]", detail.GetProperty("log").GetString());
        Assert.Equal("********", detail.GetProperty("variables").GetProperty("minio_root_password").GetString()); // masked
    }

    [Fact]
    public async Task DeploymentFailsPrerequisiteAndVariableChecks()
    {
        var admin = await AdminClientAsync();
        var (_, noAnsible) = await RegisterAgentAsync("no-ansible", "linux");
        var (_, ok) = await RegisterAgentAsync("has-ansible", "linux", "ansible", "root");

        var prereq = await admin.PostAsJsonAsync("/api/deployments", new
        {
            applicationName = "minio", hostIds = new[] { noAnsible },
            variables = new Dictionary<string, string> { ["minio_root_password"] = "SuperSecret123" }
        });
        Assert.Equal((HttpStatusCode)422, prereq.StatusCode);

        var badVar = await admin.PostAsJsonAsync("/api/deployments", new
        {
            applicationName = "minio", hostIds = new[] { ok },
            variables = new Dictionary<string, string> { ["minio_root_password"] = "SuperSecret123", ["minio_data_dir"] = "/tmp/../etc" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badVar.StatusCode);
    }
}
