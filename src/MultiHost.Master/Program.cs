using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MultiHost.Master.Data;
using MultiHost.Master.Hubs;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.Configure<SecurityOptions>(cfg.GetSection("Security"));
builder.Services.Configure<AdminOptions>(cfg.GetSection("Admin"));
builder.Services.Configure<MonitoringOptions>(cfg.GetSection("Monitoring"));
builder.Services.Configure<PlaybookOptions>(cfg.GetSection("Playbooks"));

builder.Services.AddDbContext<MasterDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IStatusNotifier, StatusNotifier>();
builder.Services.AddScoped<IHostService, HostService>();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddScoped<PlaybookLoader>();
builder.Services.AddScoped<AdminSeeder>();
builder.Services.AddHostedService<HostMonitorService>();

// ---- Authentication: JWT bearer for both admins (role=Admin) and agents (role=Agent) ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<SecurityOptions>>((o, sec) =>
    {
        var s = sec.Value;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = s.Issuer,
            ValidateAudience = true, ValidAudience = TokenService.Audience,
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s.JwtKey)),
            RoleClaimType = "role", NameClaimType = "sub", ClockSkew = TimeSpan.FromSeconds(30)
        };
        o.Events = new JwtBearerEvents
        {
            // SignalR (WebSocket) cannot set headers, so it passes the token in the query string.
            OnMessageReceived = ctx =>
            {
                var t = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = t;
                return Task.CompletedTask;
            },
            // Deregistering a host immediately revokes its (otherwise long-lived) agent token.
            OnTokenValidated = async ctx =>
            {
                if (ctx.Principal?.IsInRole(Roles.Agent) != true) return;
                var db = ctx.HttpContext.RequestServices.GetRequiredService<MasterDbContext>();
                if (!Guid.TryParse(ctx.Principal.FindFirst("hostId")?.Value, out var hid) || !await db.Hosts.AnyAsync(h => h.Id == hid))
                    ctx.Fail("Host is not registered.");
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Multi-Host Controller API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
        Description = "Obtain a token from POST /api/auth/login"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
                                   "style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:";
    await next();
});

if (app.Environment.IsDevelopment() || cfg.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();
app.MapHub<StatusHub>("/hubs/status");

// ---- Startup: schema, admin user, application packages ----
using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    if (cfg["Security:JwtKey"]?.StartsWith("dev-only") == true)
        log.LogWarning("Security:JwtKey is the development default. Set a strong secret before exposing this service.");

    var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); break; }
        catch (Exception ex) when (attempt < 15)
        {
            log.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(2000);
        }
    }
    await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync();
    var loaded = await scope.ServiceProvider.GetRequiredService<PlaybookLoader>().LoadAsync();
    log.LogInformation("Loaded {Count} application package(s)", loaded);
}

app.Run();

public partial class Program { } // exposed for WebApplicationFactory in tests
