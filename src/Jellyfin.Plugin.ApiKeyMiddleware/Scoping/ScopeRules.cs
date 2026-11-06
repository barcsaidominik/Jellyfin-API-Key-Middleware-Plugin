using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

public enum ScopeMode
{
    // No checks, nothing recorded.
    Allow,

    // Everything allowed, every request recorded.
    Audit,

    // Only requests matching a rule are allowed; all requests recorded.
    Enforce
}

public enum ScopeDecision
{
    // Not subject to scoping (not an API key, or mode Allow).
    Pass,
    Audited,
    Allowed,
    Denied
}

public sealed record KeyScope(ScopeMode Mode, IReadOnlyList<RulePattern> Rules);

// Text format, one item per line ('#' starts a comment):
//   [<api key name>] allow|audit|enforce     section header; "[*]" applies to keys without a section
//   <METHOD> /path/with/*/segments           METHOD: GET, POST, ... or *; "*" = one segment, "**" (last) = the rest
// Key names and paths are case-insensitive. Invalid input fails closed: a header with a missing or
// unknown mode becomes "enforce", an invalid rule line is dropped (so it allows nothing).
public sealed partial class ScopeRules
{
    public const string DefaultSectionName = "*";

    private readonly Dictionary<string, KeyScope> _scopes;

    private ScopeRules(Dictionary<string, KeyScope> scopes, IReadOnlyList<string> errors)
    {
        _scopes = scopes;
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    public static ScopeRules Parse(string? text)
    {
        var scopes = new Dictionary<string, (ScopeMode Mode, List<RulePattern> Rules)>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        List<RulePattern>? current = null;

        var lines = (text ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lineNo = i + 1;
            var header = HeaderRegex().Match(line);
            if (header.Success)
            {
                var name = header.Groups["name"].Value.Trim();
                if (!TryParseMode(header.Groups["mode"].Value, out var mode))
                {
                    errors.Add(Format(lineNo, $"missing or unknown mode for [{name}], using enforce"));
                }

                if (scopes.ContainsKey(name))
                {
                    errors.Add(Format(lineNo, $"duplicate section [{name}], using the later one"));
                }

                current = [];
                scopes[name] = (mode, current);
                continue;
            }

            if (current is null)
            {
                errors.Add(Format(lineNo, "rule outside of a [key] section, ignored"));
                continue;
            }

            if (RulePattern.TryParse(line, out var rule, out var error))
            {
                current.Add(rule);
            }
            else
            {
                errors.Add(Format(lineNo, $"{error}, ignored"));
            }
        }

        var result = scopes.ToDictionary(s => s.Key, s => new KeyScope(s.Value.Mode, s.Value.Rules), StringComparer.OrdinalIgnoreCase);
        return new ScopeRules(result, errors);
    }

    // Keys without a section and without a [*] section are audited.
    public KeyScope ScopeFor(string keyName)
        => _scopes.GetValueOrDefault(keyName.Trim())
            ?? _scopes.GetValueOrDefault(DefaultSectionName)
            ?? new KeyScope(ScopeMode.Audit, []);

    public ScopeDecision Decide(string keyName, string method, IReadOnlyList<string> pathSegments)
    {
        var scope = ScopeFor(keyName);
        return scope.Mode switch
        {
            ScopeMode.Allow => ScopeDecision.Pass,
            ScopeMode.Audit => ScopeDecision.Audited,
            _ => scope.Rules.Any(r => r.Matches(method, pathSegments)) ? ScopeDecision.Allowed : ScopeDecision.Denied
        };
    }

    private static bool TryParseMode(string value, out ScopeMode mode)
    {
        mode = ScopeMode.Enforce;
        // All-letters check: Enum.TryParse would also accept "1" or "audit,enforce".
        if (value.Length == 0 || !value.All(char.IsAsciiLetter) || !Enum.TryParse(value, ignoreCase: true, out ScopeMode parsed))
        {
            return false;
        }

        mode = parsed;
        return true;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }

    private static string Format(int lineNo, string message)
        => string.Create(CultureInfo.InvariantCulture, $"line {lineNo}: {message}");

    [GeneratedRegex(@"^\[(?<name>[^\]]+)\]\s*(?<mode>\S*)$")]
    private static partial Regex HeaderRegex();
}

public sealed class RulePattern
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"
    };

    private readonly string _method;
    private readonly string[] _segments;

    private RulePattern(string method, string[] segments)
    {
        _method = method;
        _segments = segments;
    }

    public static bool TryParse(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RulePattern? pattern, out string? error)
    {
        pattern = null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            error = $"expected '<METHOD> /path', got '{line}'";
            return false;
        }

        if (!Methods.Contains(parts[0]))
        {
            error = $"unknown method '{parts[0]}'";
            return false;
        }

        if (!parts[1].StartsWith('/') || parts[1].Contains('?', StringComparison.Ordinal))
        {
            error = $"path must start with '/' and have no query string: '{parts[1]}'";
            return false;
        }

        var segments = RequestPath.Split(parts[1]);
        var globstar = Array.IndexOf(segments, "**");
        if (globstar >= 0 && globstar != segments.Length - 1)
        {
            error = $"'**' is only allowed as the last segment: '{parts[1]}'";
            return false;
        }

        error = null;
        pattern = new RulePattern(parts[0].ToUpperInvariant(), segments);
        return true;
    }

    public bool Matches(string method, IReadOnlyList<string> path)
    {
        // HEAD is served by the GET handler and returns no body, so a GET rule covers it.
        var methodOk = _method == "*"
            || string.Equals(_method, method, StringComparison.OrdinalIgnoreCase)
            || (_method == "GET" && string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
        if (!methodOk)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] == "**")
            {
                return path.Count >= i;
            }

            if (i >= path.Count
                || (_segments[i] != "*" && !string.Equals(_segments[i], path[i], StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return path.Count == _segments.Length;
    }

    public override string ToString() => $"{_method} /{string.Join('/', _segments)}";
}
