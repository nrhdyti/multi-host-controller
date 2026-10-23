using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MultiHost.Shared;

namespace MultiHost.Agent.Services;

/// <summary>Typed HTTP client for the master's agent API. All traffic is agent-initiated.</summary>
public class MasterClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    public string? Token { get; set; }

    public MasterClient(HttpClient http) => _http = http;

    public async Task<RegisterAgentResponse> RegisterAsync(RegisterAgentRequest req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/agent/register", req, Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RegisterAgentResponse>(Json, ct))!;
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest hb, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, "api/agent/heartbeat", hb, ct);
        return (await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(Json, ct))!;
    }

    public async Task<AgentJob?> NextJobAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, "api/agent/jobs/next", null, ct);
        return resp.StatusCode == HttpStatusCode.NoContent ? null : await resp.Content.ReadFromJsonAsync<AgentJob>(Json, ct);
    }

    public async Task AppendLogAsync(Guid jobId, string text, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"api/agent/jobs/{jobId}/log", new JobLogChunk { Text = text }, ct);
    }

    public async Task CompleteAsync(Guid jobId, JobResultRequest result, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"api/agent/jobs/{jobId}/result", result, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            resp.Dispose();
            throw new Models.AgentRevokedException();
        }
        resp.EnsureSuccessStatusCode();
        return resp;
    }
}
