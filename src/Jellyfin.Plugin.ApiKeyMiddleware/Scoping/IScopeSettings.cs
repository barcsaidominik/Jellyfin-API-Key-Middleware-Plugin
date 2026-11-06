using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

public interface IScopeSettings
{
    // Null while the plugin is not initialised: the middleware then lets everything through.
    string? Rules { get; }

    string? BaseUrl { get; }
}

// Read on every request so a saved configuration takes effect without a restart.
internal sealed class JellyfinScopeSettings : IScopeSettings
{
    private readonly IServerConfigurationManager _configurationManager;

    public JellyfinScopeSettings(IServerConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    public string? Rules => Plugin.Instance?.Configuration.Rules;

    public string? BaseUrl => _configurationManager.GetNetworkConfiguration().BaseUrl;
}
