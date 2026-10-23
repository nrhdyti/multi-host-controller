using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MultiHost.Master.Infrastructure;

namespace MultiHost.Master.Hubs;

[Authorize(Roles = Roles.Admin)]
public class StatusHub : Hub { }
