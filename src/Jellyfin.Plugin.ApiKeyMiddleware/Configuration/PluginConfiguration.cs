using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Scope rules in the text format parsed by ScopeRules (see README). Default: every API key is
    // allowed and its requests are recorded, so allowlists can be built from real traffic.
    public const string DefaultRules = "[*] audit";

    public string Rules { get; set; } = DefaultRules;
}
