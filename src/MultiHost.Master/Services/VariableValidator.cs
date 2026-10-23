using System.Text.RegularExpressions;
using MultiHost.Master.Infrastructure;
using MultiHost.Master.Models;

namespace MultiHost.Master.Services;

/// <summary>
/// Validates deployment variables against an application's declared schema. Every value that reaches an agent
/// has passed through here: unknown keys are rejected, types/ranges/patterns are enforced and control
/// characters are refused. Values are passed to Ansible via a JSON vars file, never interpolated into a shell.
/// </summary>
public static class VariableValidator
{
    public static readonly HashSet<string> Types = new() { "string", "int", "port", "path", "bool", "enum" };
    private static readonly Regex PathRx = new(@"^/[A-Za-z0-9_./-]*$", RegexOptions.Compiled);

    public static Dictionary<string, string> Resolve(IReadOnlyList<VariableDef> defs, IReadOnlyDictionary<string, string>? input)
    {
        var errors = new List<string>();
        var result = new Dictionary<string, string>();
        input ??= new Dictionary<string, string>();

        foreach (var key in input.Keys)
            if (!defs.Any(d => d.Name == key)) errors.Add($"Unknown variable '{key}'.");

        foreach (var d in defs)
        {
            input.TryGetValue(d.Name, out var v);
            if (string.IsNullOrEmpty(v)) v = d.Default;
            if (string.IsNullOrEmpty(v))
            {
                if (d.Required) errors.Add($"Variable '{d.Name}' is required.");
                continue;
            }
            var err = Validate(d, v);
            if (err is null) result[d.Name] = v; else errors.Add($"'{d.Name}': {err}");
        }

        if (errors.Count > 0) throw new ApiException(400, string.Join(" ", errors));
        return result;
    }

    private static string? Validate(VariableDef d, string v)
    {
        if (v.Length > 512) return "value too long.";
        if (v.Any(char.IsControl)) return "control characters are not allowed.";

        switch (d.Type)
        {
            case "int":
                if (!long.TryParse(v, out var n)) return "must be an integer.";
                if (d.Min is { } min && n < min) return $"must be >= {min}.";
                if (d.Max is { } max && n > max) return $"must be <= {max}.";
                break;
            case "port":
                if (!int.TryParse(v, out var p) || p is < 1 or > 65535) return "must be a port between 1 and 65535.";
                break;
            case "bool":
                if (v is not ("true" or "false")) return "must be 'true' or 'false'.";
                break;
            case "path":
                if (!PathRx.IsMatch(v) || v.Contains("..") || v.Contains("//")) return "must be a clean absolute path ([A-Za-z0-9_./-], no '..').";
                break;
            case "enum":
                if (d.Options is null || !d.Options.Contains(v)) return $"must be one of: {string.Join(", ", d.Options ?? new())}.";
                break;
        }

        if (!string.IsNullOrEmpty(d.Pattern))
        {
            try { if (!Regex.IsMatch(v, d.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250))) return "does not match the required format."; }
            catch (RegexMatchTimeoutException) { return "could not be validated."; }
        }
        return null;
    }
}
