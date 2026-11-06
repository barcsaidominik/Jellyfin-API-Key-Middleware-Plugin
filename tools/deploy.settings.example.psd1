# Copy to deploy.settings.psd1 (git-ignored) and adjust. Any parameter of Deploy-Plugin.ps1 can be set here;
# command-line arguments win.
@{
    Server        = 'jellyfin-host'                            # PowerShell remoting target
    ApiKeyFile    = 'C:\ProgramData\Jellyfin\deploy.key'       # on the server: the 'deploy' API key
    # PluginsDir  = 'C:\ProgramData\Jellyfin\Server\plugins'
    # JellyfinUrl = 'http://localhost:8096'                    # as seen from the server
    # StartTaskPath = '\'; StartTaskName = 'Jellyfin'          # if Jellyfin runs as a scheduled task
    # ServiceName = 'JellyfinServer'                           # otherwise the Windows service is started
}
