using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace MultiHost.Master.Infrastructure;

/// <summary>Domain/validation failure that maps to an HTTP status code.</summary>
public class ApiException : Exception
{
    public int Status { get; }
    public ApiException(int status, string message) : base(message) => Status = status;
}

public class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _log;
    private readonly IHostEnvironment _env;
    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> log, IHostEnvironment env)
    { _next = next; _log = log; _env = env; }

    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (ApiException ex)
        {
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = ex.Status, Title = ex.Message });
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            _log.LogError(ex, "Unhandled exception for {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 500, Title = "Internal server error",
                Detail = _env.IsDevelopment() ? ex.ToString() : null // never leak internals in production
            });
        }
    }
}

public static class ClaimsExtensions
{
    public static Guid HostId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirst("hostId")?.Value, out var id) ? id : throw new ApiException(401, "Missing host claim.");
    public static string UserName(this ClaimsPrincipal p) => p.FindFirst("sub")?.Value ?? "unknown";
}
