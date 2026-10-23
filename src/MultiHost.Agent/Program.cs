using Microsoft.Extensions.Options;
using MultiHost.Agent.Models;
using MultiHost.Agent.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
var opt = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

// Local logging: console + rolling file.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(opt.LogDirectory, "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

// Run as a Windows Service / systemd unit when hosted by one; both calls are no-ops otherwise.
builder.Services.AddWindowsService(o => o.ServiceName = "MultiHostAgent");
builder.Services.AddSystemd();

builder.Services.AddSingleton<AgentStateStore>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<AnsibleRunner>();
builder.Services.AddSingleton<JobExecutor>();
// One shared MasterClient: it holds the bearer token used by both the worker and the job executor.
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    if (o.AllowInvalidCertificate) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    var http = new HttpClient(handler, disposeHandler: true)
    {
        BaseAddress = new Uri(o.MasterUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    return new MasterClient(http);
});
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
