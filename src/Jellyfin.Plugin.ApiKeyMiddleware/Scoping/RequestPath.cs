using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

public static partial class RequestPath
{
    public static string[] Split(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    // The middleware runs before Jellyfin maps its BaseUrl, so the raw path still carries it.
    // Returns null for paths outside the BaseUrl (Jellyfin does not serve the API there).
    public static string[]? Relative(string? path, string? baseUrl)
    {
        var segments = Split(path ?? string.Empty);
        var prefix = Split(baseUrl ?? string.Empty);
        if (segments.Length < prefix.Length)
        {
            return null;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(segments[i], prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return segments[prefix.Length..];
    }

    // For the observed list: ids become "*", so one endpoint shows up once and the line can be
    // pasted as a rule. The path is caller-controlled and already percent-decoded, so control
    // characters (e.g. a decoded %0A) are replaced: the template ends up in log lines.
    public static string Template(IReadOnlyList<string> segments)
        => "/" + string.Join('/', segments.Select(s => IdSegmentRegex().IsMatch(s) ? "*" : WithoutControlChars(s)));

    private static string WithoutControlChars(string segment)
        => segment.Any(char.IsControl)
            ? string.Concat(segment.Select(c => char.IsControl(c) ? '_' : c))
            : segment;

    // GUIDs (with or without dashes), long hex strings and numbers.
    [GeneratedRegex(@"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{16,}|\d+)$")]
    private static partial Regex IdSegmentRegex();
}
