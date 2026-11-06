namespace Jellyfin.Plugin.ApiKeyMiddleware.Configuration;

// Jellyfin replaces a plugin configuration it cannot read (broken XML, disk error) with the defaults
// and saves them over the file. For this plugin that would silently turn every key back into a full
// administrator ("[*] audit") and lose the rules. So the rules saved by the admin are also kept in a
// plain text file next to the configuration, and restored when the configuration comes back as the
// factory default although different rules were saved before.
internal sealed class RulesBackup
{
    private readonly string _path;

    public RulesBackup(string path)
    {
        _path = path;
    }

    public string Path => _path;

    // Returns the rules to restore, or null if the configuration was not reset.
    public string? RulesToRestore(string? currentRules)
    {
        if (!IsFactoryDefault(currentRules) || !File.Exists(_path))
        {
            return null;
        }

        var saved = File.ReadAllText(_path);
        return IsFactoryDefault(saved) ? null : saved;
    }

    public void Save(string? rules)
    {
        rules ??= string.Empty;
        if (File.Exists(_path) && string.Equals(File.ReadAllText(_path), rules, StringComparison.Ordinal))
        {
            return;
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, rules);
        File.Move(temp, _path, overwrite: true);
    }

    private static bool IsFactoryDefault(string? rules)
        => string.Equals((rules ?? string.Empty).Trim(), PluginConfiguration.DefaultRules, StringComparison.Ordinal);
}
