# Builds the plugin and installs it on a Windows Jellyfin server over PowerShell remoting (run on the dev machine).
#   .\tools\Deploy-Plugin.ps1 -Server myserver                # build + copy DLL only (fails if this version is already loaded)
#   .\tools\Deploy-Plugin.ps1 -Server myserver -Restart       # stop Jellyfin, copy, start again (refuses while something is playing)
# Parameters not given on the command line are read from tools/deploy.settings.psd1 if it exists
# (git-ignored; see deploy.settings.example.psd1), so machine-specific values stay out of the repository.
# -Restart uses a dedicated 'deploy' API key, read on the server from -ApiKeyFile and never printed. If that key
# is scoped by this plugin in enforce mode, it needs: GET /Sessions, POST /System/Shutdown, GET /Plugins.
# Jellyfin is started again with the scheduled task -StartTaskPath/-StartTaskName if given, otherwise with the
# Windows service -ServiceName.
param(
    [string]$Server,
    [string]$ApiKeyFile,
    [string]$PluginsDir = 'C:\ProgramData\Jellyfin\Server\plugins',
    [string]$JellyfinUrl = 'http://localhost:8096',
    [string]$StartTaskPath,
    [string]$StartTaskName,
    [string]$ServiceName = 'JellyfinServer',
    [switch]$Restart
)

$settingsFile = Join-Path $PSScriptRoot 'deploy.settings.psd1'
if (Test-Path $settingsFile) {
    $settings = Import-PowerShellDataFile $settingsFile
    foreach ($name in $settings.Keys) {
        if (-not $PSBoundParameters.ContainsKey($name)) { Set-Variable -Name $name -Value $settings[$name] }
    }
}

if (-not $Server) { throw 'Server not set: pass -Server or create tools/deploy.settings.psd1' }
if ($Restart -and -not $ApiKeyFile) { throw '-Restart needs -ApiKeyFile (the path of the deploy API key file on the server)' }

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Jellyfin.Plugin.ApiKeyMiddleware'

dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dll = Join-Path $project 'bin\Release\net10.0\Jellyfin.Plugin.ApiKeyMiddleware.dll'
$version = (Get-Item $dll).VersionInfo.FileVersion
$target = Join-Path $PluginsDir "ApiKeyMiddleware_$version"

$session = New-PSSession -ComputerName $Server
try {
    # The loaded plugin DLL is locked, so with -Restart Jellyfin is stopped before copying.
    if ($Restart) {
        Invoke-Command $session {
            param($keyFile, $url)
            if (-not (Test-Path $keyFile)) { throw "Deploy API key file not found on the server: $keyFile (Dashboard -> API Keys -> new key 'deploy', save it into this file)" }
            $key = (Get-Content $keyFile -Raw).Trim()
            $headers = @{ Authorization = "MediaBrowser Token=`"$key`"" }
            # PS 5.1 emits a JSON array as one pipeline object, so unroll it explicitly.
            $sessions = Invoke-RestMethod "$url/Sessions" -Headers $headers
            $playing = @(foreach ($s in $sessions) { if ($s.NowPlayingItem) { $s } })
            if ($playing.Count -gt 0) { throw "Not restarting: $($playing.Count) active playback session(s)" }

            Invoke-RestMethod "$url/System/Shutdown" -Method Post -Headers $headers | Out-Null
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Process jellyfin -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
            if (Get-Process jellyfin -ErrorAction SilentlyContinue) { throw 'Jellyfin did not stop within 60 s' }
        } -ArgumentList $ApiKeyFile, $JellyfinUrl
    }

    try {
        Invoke-Command $session { param($t) New-Item -ItemType Directory -Force $t | Out-Null } -ArgumentList $target
        Copy-Item $dll -Destination $target -ToSession $session -Force
        "Copied $([IO.Path]::GetFileName($dll)) $version -> $target"
    }
    finally {
        # Jellyfin is started again even if the copy failed.
        if ($Restart) {
            Invoke-Command $session {
                param($keyFile, $target, $pluginsDir, $url, $taskPath, $taskName, $serviceName)
                # Only the version just deployed should remain.
                Get-ChildItem $pluginsDir -Directory -Filter 'ApiKeyMiddleware_*' |
                    Where-Object { $_.FullName -ne $target } |
                    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force; "Removed old plugin folder $($_.Name)" }

                if ($taskName) { Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName } else { Start-Service $serviceName }
                $deadline = (Get-Date).AddSeconds(120)
                do {
                    Start-Sleep -Seconds 3
                    try { $health = Invoke-RestMethod "$url/health" -TimeoutSec 5 } catch { $health = $null }
                } until ($health -eq 'Healthy' -or (Get-Date) -gt $deadline)
                "Jellyfin health: $health"

                $key = (Get-Content $keyFile -Raw).Trim()
                $plugins = Invoke-RestMethod "$url/Plugins" -Headers @{ Authorization = "MediaBrowser Token=`"$key`"" }
                $plugin = foreach ($p in $plugins) { if ($p.Name -eq 'API Key Middleware') { $p } }
                "API Key Middleware plugin: $($plugin.Version) $($plugin.Status)"
            } -ArgumentList $ApiKeyFile, $target, $PluginsDir, $JellyfinUrl, $StartTaskPath, $StartTaskName, $ServiceName
        }
    }
}
finally {
    Remove-PSSession $session
}
