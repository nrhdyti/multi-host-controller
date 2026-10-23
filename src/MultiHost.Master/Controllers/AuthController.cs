using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using MultiHost.Master.Services;

namespace MultiHost.Master.Controllers;

[ApiController, Route("api/auth")]
public class AuthController : ControllerBase
{
    private static readonly string DummyHash = Crypto.HashPassword("dummy-password-for-timing");
    private readonly MasterDbContext _db;
    private readonly TokenService _tokens;
    private readonly ILogger<AuthController> _log;
    public AuthController(MasterDbContext db, TokenService tokens, ILogger<AuthController> log) { _db = db; _tokens = tokens; _log = log; }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(u => u.Username == req.Username, ct);
        var ok = Crypto.VerifyPassword(req.Password, user?.PasswordHash ?? DummyHash) && user is not null;
        if (!ok) { _log.LogWarning("Failed login for {User}", req.Username); return Unauthorized(new ProblemDetails { Title = "Invalid credentials" }); }
        var (token, exp) = _tokens.CreateAdminToken(user!);
        return new LoginResponse(token, exp);
    }
}
