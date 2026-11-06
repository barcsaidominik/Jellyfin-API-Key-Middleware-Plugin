using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Scope rules in the text format parsed by ScopeRules (see README). Default: every API key is
    // allowed and its requests are recorded, so allowlists can be built from real traffic.
    public string Rules { get; set; } = "[*] audit";
}
