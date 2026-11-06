namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

// Re-parses only when the configured text changes (saving the plugin page replaces the string).
public sealed class RulesCache
{
    private Parsed? _current;

    public ScopeRules Get(string? text)
    {
        var current = Volatile.Read(ref _current);
        if (current is not null && string.Equals(current.Text, text, StringComparison.Ordinal))
        {
            return current.Rules;
        }

        var parsed = new Parsed(text, ScopeRules.Parse(text));
        Volatile.Write(ref _current, parsed);
        return parsed.Rules;
    }

    private sealed record Parsed(string? Text, ScopeRules Rules);
}
