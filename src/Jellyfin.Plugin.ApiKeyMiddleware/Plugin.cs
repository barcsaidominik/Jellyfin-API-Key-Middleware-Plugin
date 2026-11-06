using Jellyfin.Plugin.ApiKeyMiddleware.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Jellyfin.Plugin.ApiKeyMiddleware;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly RulesBackup _rulesBackup;
    private readonly ILogger<Plugin> _logger;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        _rulesBackup = new RulesBackup(Path.ChangeExtension(ConfigurationFilePath, ".rules.txt"));
        RestoreOrBackUpRules();
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "API Key Middleware";

    public override Guid Id => Guid.Parse("05d59699-6b9e-41c1-a9d7-15a8b78865df");

    public override string Description => "Per-API-key endpoint allowlist (HTTP method + path) for the Jellyfin API.";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        BackUpRules();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }

    // A failure here must not stop the plugin from loading: without it every key has full access.
    private void RestoreOrBackUpRules()
    {
        try
        {
            var restore = _rulesBackup.RulesToRestore(Configuration.Rules);
            if (restore is null)
            {
                _rulesBackup.Save(Configuration.Rules);
                return;
            }

            Configuration.Rules = restore;
            SaveConfiguration();
            _logger.LogWarning(
                "The plugin configuration was reset to the factory default (\"{Default}\"), probably because Jellyfin could not read it. The last saved rules were restored from {BackupPath}.",
                PluginConfiguration.DefaultRules,
                _rulesBackup.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not check or update the rules backup {BackupPath}", _rulesBackup.Path);
        }
    }

    private void BackUpRules()
    {
        try
        {
            _rulesBackup.Save(Configuration.Rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not update the rules backup {BackupPath}", _rulesBackup.Path);
        }
    }
}
