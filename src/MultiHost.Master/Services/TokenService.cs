using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;

namespace MultiHost.Master.Services;

public class TokenService
{
    public const string Audience = "multihost";
    private readonly SecurityOptions _s;
    private readonly SigningCredentials _creds;

    public TokenService(IOptions<SecurityOptions> options)
    {
        _s = options.Value;
        if (_s.JwtKey.Length < 32) throw new InvalidOperationException("Security:JwtKey must be at least 32 characters.");
        _creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.JwtKey)), SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTime Expires) CreateAdminToken(AdminUser user) =>
        Create(new[] { new Claim("sub", user.Username), new Claim("role", Roles.Admin) }, TimeSpan.FromMinutes(_s.AdminTokenMinutes));

    public string CreateAgentToken(Guid hostId) =>
        Create(new[]
        {
            new Claim("sub", $"agent:{hostId}"), new Claim("role", Roles.Agent), new Claim("hostId", hostId.ToString())
        }, TimeSpan.FromDays(_s.AgentTokenDays)).Token;

    private (string Token, DateTime Expires) Create(IEnumerable<Claim> claims, TimeSpan life)
    {
        var now = DateTime.UtcNow;
        var exp = now.Add(life);
        var all = claims.Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));
        var jwt = new JwtSecurityToken(_s.Issuer, Audience, all, now, exp, _creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), exp);
    }
}
