using Microsoft.AspNetCore.SignalR;
using MultiHost.Master.Hubs;

namespace MultiHost.Master.Services;

/// <summary>Pushes change notifications to dashboards. Clients refetch via REST, so payloads stay tiny.</summary>
public interface IStatusNotifier
{
    Task HostsChangedAsync();
    Task DeploymentChangedAsync(Guid deploymentId);
}

public class StatusNotifier : IStatusNotifier
{
    private readonly IHubContext<StatusHub> _hub;
    public StatusNotifier(IHubContext<StatusHub> hub) => _hub = hub;
    public Task HostsChangedAsync() => _hub.Clients.All.SendAsync("hostsChanged");
    public Task DeploymentChangedAsync(Guid id) => _hub.Clients.All.SendAsync("deploymentChanged", id);
}
