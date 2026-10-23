using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MultiHost.Master.Data;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MultiHost.Master.Services;

public interface IApplicationService
{
    Task<List<ApplicationDto>> ListAsync(CancellationToken ct);
    Task<ApplicationDto?> GetAsync(string name, CancellationToken ct);
    Task<ApplicationDto> UpsertAsync(PackageUpsertRequest req, CancellationToken ct);
}

public class ApplicationService : IApplicationService
{
    private static readonly Regex NameRx = new(@"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);
    private readonly MasterDbContext _db;
    private readonly ILogger<ApplicationService> _log;
    public ApplicationService(MasterDbContext db, ILogger<ApplicationService> log) { _db = db; _log = log; }

    public async Task<List<ApplicationDto>> ListAsync(CancellationToken ct) =>
        (await _db.Applications.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<ApplicationDto?> GetAsync(string name, CancellationToken ct)
    {
        var a = await _db.Applications.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, ct);
        return a is null ? null : ToDto(a);
    }

    public async Task<ApplicationDto> UpsertAsync(PackageUpsertRequest req, CancellationToken ct)
    {
        ValidateYaml("installPlaybook", req.InstallPlaybook);
        if (req.VerifyPlaybook is not null) ValidateYaml("verifyPlaybook", req.VerifyPlaybook);
        if (req.RollbackPlaybook is not null) ValidateYaml("rollbackPlaybook", req.RollbackPlaybook);
        foreach (var v in req.Variables)
        {
            if (!NameRx.IsMatch(v.Name)) throw new ApiException(400, $"Invalid variable name '{v.Name}'.");
            if (!VariableValidator.Types.Contains(v.Type)) throw new ApiException(400, $"Variable '{v.Name}' has unknown type '{v.Type}'.");
            if (!string.IsNullOrEmpty(v.Pattern))
            {
                try { _ = new Regex(v.Pattern); } catch (ArgumentException) { throw new ApiException(400, $"Variable '{v.Name}' has an invalid pattern."); }
            }
        }
        if (req.Variables.Select(v => v.Name).Distinct().Count() != req.Variables.Count)
            throw new ApiException(400, "Duplicate variable names.");

        var app = await _db.Applications.FirstOrDefaultAsync(a => a.Name == req.Name, ct);
        if (app is null) { app = new ApplicationPackage { Id = Guid.NewGuid(), Name = req.Name }; _db.Applications.Add(app); }
        app.Description = req.Description;
        app.Version = req.Version;
        app.InstallPlaybook = req.InstallPlaybook;
        app.VerifyPlaybook = req.VerifyPlaybook;
        app.RollbackPlaybook = req.RollbackPlaybook;
        app.VariablesJson = JsonSerializer.Serialize(req.Variables);
        app.RequiredCapabilities = string.Join(",", req.RequiredCapabilities.Select(c => c.Trim().ToLowerInvariant()));
        app.Dependencies = string.Join(",", req.Dependencies.Select(c => c.Trim().ToLowerInvariant()));
        app.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Application package {Name} v{Version} saved", app.Name, app.Version);
        return ToDto(app);
    }

    private static void ValidateYaml(string field, string yaml)
    {
        try { new DeserializerBuilder().Build().Deserialize<object>(yaml); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ApiException(400, $"{field} is not valid YAML: {ex.Message}"); }
    }

    public static List<VariableDef> ParseVariables(string json) =>
        JsonSerializer.Deserialize<List<VariableDef>>(json) ?? new();

    private static string[] Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static ApplicationDto ToDto(ApplicationPackage a) => new(a.Name, a.Description, a.Version,
        Split(a.RequiredCapabilities), Split(a.Dependencies), ParseVariables(a.VariablesJson),
        !string.IsNullOrWhiteSpace(a.VerifyPlaybook), !string.IsNullOrWhiteSpace(a.RollbackPlaybook), a.UpdatedAt);
}

/// <summary>Loads application packages (manifest.yml + playbooks) from the playbooks directory at startup.</summary>
public class PlaybookLoader
{
    private class Manifest
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "1.0.0";
        public List<string> RequiredCapabilities { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();
        public string Install { get; set; } = "install.yml";
        public string? Verify { get; set; } = "verify.yml";
        public string? Rollback { get; set; } = "rollback.yml";
        public List<VariableDef> Variables { get; set; } = new();
    }

    private readonly IApplicationService _apps;
    private readonly PlaybookOptions _opt;
    private readonly ILogger<PlaybookLoader> _log;
    public PlaybookLoader(IApplicationService apps, IOptions<PlaybookOptions> opt, ILogger<PlaybookLoader> log)
    { _apps = apps; _opt = opt.Value; _log = log; }

    public async Task<int> LoadAsync(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_opt.Directory);
        if (!Directory.Exists(root)) { _log.LogWarning("Playbook directory {Dir} not found", root); return 0; }

        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var count = 0;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            if (Path.GetFileName(dir).StartsWith('_')) continue; // templates
            var mf = Path.Combine(dir, "manifest.yml");
            if (!File.Exists(mf)) continue;
            try
            {
                var m = yaml.Deserialize<Manifest>(await File.ReadAllTextAsync(mf, ct));
                await _apps.UpsertAsync(new PackageUpsertRequest
                {
                    Name = m.Name, Description = m.Description, Version = m.Version,
                    InstallPlaybook = await ReadAsync(dir, m.Install, ct) ?? throw new InvalidOperationException("install playbook missing"),
                    VerifyPlaybook = await ReadAsync(dir, m.Verify, ct),
                    RollbackPlaybook = await ReadAsync(dir, m.Rollback, ct),
                    Variables = m.Variables, RequiredCapabilities = m.RequiredCapabilities, Dependencies = m.Dependencies
                }, ct);
                count++;
            }
            catch (Exception ex) { _log.LogError(ex, "Failed to load application package from {Dir}", dir); }
        }
        return count;
    }

    private static async Task<string?> ReadAsync(string dir, string? file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var full = Path.GetFullPath(Path.Combine(dir, file));
        if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar)) throw new InvalidOperationException("Path escapes package directory.");
        return File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null;
    }
}
