using Jellyfin.Plugin.ApiKeyMiddleware.Builder;
using Jellyfin.Plugin.ApiKeyMiddleware.Scoping;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.ApiKeyMiddleware;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    // Plugin services land in the host's own container before the web host is built, so the
    // startup filter is applied when Jellyfin builds its request pipeline.
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<IScopeSettings, JellyfinScopeSettings>();
        serviceCollection.AddSingleton<RulesCache>();
        serviceCollection.AddSingleton<ObservedRequests>();
        serviceCollection.AddSingleton<XmlDocSummaries>();
        serviceCollection.AddTransient<IStartupFilter, ApiKeyScopeStartupFilter>();
    }
}
