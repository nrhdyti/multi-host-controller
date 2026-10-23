using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using MultiHost.Master.Services;
using Xunit;

namespace MultiHost.Master.Tests;

public class VariableValidatorTests
{
    private static readonly List<VariableDef> Defs = new()
    {
        new() { Name = "port", Type = "port", Default = "9000" },
        new() { Name = "dir", Type = "path", Default = "/var/lib/app" },
        new() { Name = "user", Type = "string", Required = true, Pattern = "^[a-z]{3,10}$" },
        new() { Name = "pw", Type = "string", Required = true, Secret = true },
        new() { Name = "mode", Type = "enum", Options = new() { "a", "b" }, Default = "a" },
        new() { Name = "flag", Type = "bool", Default = "false" },
    };

    private static Dictionary<string, string> Valid() => new() { ["user"] = "alice", ["pw"] = "secret-pass" };

    [Fact]
    public void AppliesDefaults()
    {
        var r = VariableValidator.Resolve(Defs, Valid());
        Assert.Equal("9000", r["port"]);
        Assert.Equal("/var/lib/app", r["dir"]);
        Assert.Equal("a", r["mode"]);
    }

    [Theory]
    [InlineData("port", "0")]
    [InlineData("port", "70000")]
    [InlineData("port", "abc")]
    [InlineData("dir", "relative/path")]
    [InlineData("dir", "/var/../etc")]
    [InlineData("dir", "/tmp/x; rm -rf /")]
    [InlineData("user", "Bad User")]
    [InlineData("mode", "c")]
    [InlineData("flag", "yes")]
    public void RejectsInvalidValues(string name, string value)
    {
        var input = Valid(); input[name] = value;
        var ex = Assert.Throws<ApiException>(() => VariableValidator.Resolve(Defs, input));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void RejectsUnknownAndMissingRequired()
    {
        Assert.Throws<ApiException>(() => VariableValidator.Resolve(Defs, new Dictionary<string, string>() { ["user"] = "alice", ["pw"] = "x", ["evil"] = "1" }));
        Assert.Throws<ApiException>(() => VariableValidator.Resolve(Defs, new Dictionary<string, string>() { ["user"] = "alice" }));
    }

    [Fact]
    public void RejectsNewlinesInValues()
    {
        var input = Valid(); input["pw"] = "abc\nMINIO_X=1";
        Assert.Throws<ApiException>(() => VariableValidator.Resolve(Defs, input));
    }
}

public class CryptoTests
{
    [Fact]
    public void PasswordHashRoundTrips()
    {
        var h = Crypto.HashPassword("correct horse");
        Assert.True(Crypto.VerifyPassword("correct horse", h));
        Assert.False(Crypto.VerifyPassword("wrong", h));
        Assert.NotEqual(h, Crypto.HashPassword("correct horse")); // salted
    }

    [Fact]
    public void TokensAreUniqueAndUrlSafe()
    {
        var a = Crypto.RandomToken(); var b = Crypto.RandomToken();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a); Assert.DoesNotContain('/', a);
    }
}
