# Jellyfin API Key Middleware

A Jellyfin plugin that adds a per-API-key allowlist (HTTP method + path). In Jellyfin every API key is a full administrator. If a key leaks (for example from a dashboard's configuration), in enforce mode the plugin only lets it reach the endpoints the application behind it actually needs.

Early release: built and tested against Jellyfin **12.4.0**. It narrows what API keys can do; it is not a replacement for Jellyfin's own authentication, keeping Jellyfin up to date, a firewall or a VPN. Feedback, especially on bypasses and route edge cases, is welcome (see [SECURITY.md](SECURITY.md)).

**AI disclosure:** this plugin was developed primarily with an LLM (Claude, via Claude Code): most of the code, tests, documentation and the security review were written with it. The maintainer directed the work, reviewed the changes and tested every release on a live Jellyfin server. See Jellyfin's [LLM policies](https://jellyfin.org/docs/general/contributing/llm-policies/).

## How it works

- The plugin registers an `IStartupFilter` that puts a middleware at the **front** of Jellyfin's request pipeline (before authentication and routing). Plugin services are added to the host's own DI container before the web host is built (Jellyfin 12.4: `Program.cs` → `ApplicationHost.Init` → `RegisterServices`).
- Requests are identified with the same `IAuthorizationContext` Jellyfin uses itself: the `Authorization`/`X-Emby-Authorization` header and the `ApiKey` query parameter (plus the legacy `X-Emby-Token`/`X-MediaBrowser-Token` headers and `api_key` query parameter if the server still accepts them; Jellyfin 12.4 rejects them by default). Whatever way Jellyfin accepts a key, the plugin sees the same key. For API keys Jellyfin sets the `Client` field to the **key's name**, whatever the caller sends, so rules refer to key names.
- Only requests made with an API key are checked. User sessions and anonymous requests pass through unchanged.
- A denied request gets **403** with an empty body. Every new (key, method, path template) combination is logged once (Warning when denied, Information otherwise). Tokens and query strings are never logged.

## Rule builder

Dashboard → Plugins → API Key Middleware. The top of the page is a point-and-click editor for the rules:

1. Pick an API key (the list comes from Dashboard → API Keys; `*` is the section for keys without their own).
2. Tick the endpoints the application needs. The list contains every route the server serves, including the ones of other plugins and legacy routes that are hidden from the API documentation, grouped by controller and searchable, with Jellyfin's own description of each endpoint. Badges mark **admin**-only endpoints, **public** ones (callable without signing in), **legacy** routes, and the ones the key was **seen** calling since the last restart.
3. If the key's section has no rules yet, the endpoints it was seen calling are pre-selected. Review them: observed is not the same as minimal, and if the key was already misused, those calls are in the list too.
4. **Apply to rules** rewrites only this key's section in the rules text below; **Save** activates it.

Lines of a section that do not correspond to a single endpoint (e.g. `GET /Items/*/Images/**`, or comments) are shown under *Custom rules* and written back unchanged. The builder warns about selected admin-only endpoints and about an enforce section without rules (which denies everything).

The endpoint list is served by `GET /ApiKeyMiddleware/Catalog` (administrators only). It returns key **names** only, never tokens.

## Rules

The rules are a single text field on the same page; the builder above only edits it:

```
# '#' starts a comment
[homepage] enforce
GET /Sessions
GET /Items/Counts
GET /Users/*/Items/Latest

[watchstate] audit

[*] enforce          # every key without a section of its own
GET /System/Info/Public
```

- **Section:** `[key name] allow|audit|enforce`. The key name is the one shown in Dashboard → API Keys. Case does not matter. Keys with the same name share a section.
  - `allow`: no checks, nothing recorded;
  - `audit`: every request is allowed and recorded;
  - `enforce`: only requests matching a rule get through, the rest get 403.
- **Rule:** `METHOD /path`. `METHOD` is `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS` or `*`. A `GET` rule also covers `HEAD`. In the path `*` matches exactly one segment and a trailing `**` matches the rest (zero or more segments). The query string is not checked.
- **Default:** keys without a section get the `[*]` section, or `audit` if there is none. Factory setting: `[*] audit`.
- **Errors fail closed:** a missing or unknown mode becomes `enforce`, an invalid rule line is dropped (so it allows nothing). Errors are listed on the configuration page.
- Jellyfin's `BaseUrl` is stripped from the path; do not include it in rules.

### Building an allowlist

1. Leave new keys in `audit` mode (the factory setting).
2. Use the applications for a while.
3. Open the rule builder, pick the key: what it called is pre-selected. Review the list (drop anything the application should not need), switch the mode to `enforce`, apply and save.
4. Watch for denials: a denied request is logged as a Warning and shows up as *denied* in the builder and in the observed list.

The observed requests are kept in memory only (cleared on restart, at most 2000 entries). The *Observed requests* section of the page shows them in rule format as well.

## Known limitations

- **Fail-open:** if the plugin is not loaded (disabled, wrong version, after a Jellyfin upgrade), the middleware is absent and every key has full access again. After upgrading Jellyfin, check that the plugin is Active.
- **Admin above the rules:** a key in `allow` or `audit` mode can rewrite the plugin's own settings. In enforce mode only the allowed endpoints are reachable, so the settings are not.
- **One extra database lookup:** for requests with a token the middleware looks up the key as well; Jellyfin's auth handler does not reuse that result.
- **Jellyfin version:** each release is built and tested against one Jellyfin version (the first three parts of the plugin version). Jellyfin may load it on a newer server, but that is untested: check that it is Active and that a disallowed call still gets 403.
- **Not a full security boundary:** user sessions and anonymous requests are not affected, and a key in `allow`/`audit` mode is still a full administrator.

## Installation

### From the plugin repository

Dashboard → Plugins → Repositories → add:

```
https://raw.githubusercontent.com/barcsaidominik/Jellyfin-API-Key-Middleware-Plugin/main/manifest.json
```

Then install **API Key Middleware** from the catalog and restart Jellyfin. Requires Jellyfin **12.4.0**. After the restart, check Dashboard → Plugins: the plugin must be **Active**.

### Uninstalling or rolling back

Dashboard → Plugins → API Key Middleware → Uninstall, then restart Jellyfin. Every key has its normal full access again. The rules stay in `<Jellyfin data>/plugins/configurations/Jellyfin.Plugin.ApiKeyMiddleware.xml`, so reinstalling restores them. To roll back to an older release, uninstall, copy the DLL from its GitHub release zip into `<Jellyfin data>/plugins/ApiKeyMiddleware_<version>/` and restart.

### From source

Requirements: Jellyfin **12.4.0** (the `Jellyfin.Controller`/`Jellyfin.Model` package versions match the server), .NET 10 SDK.

```powershell
dotnet test
dotnet build src/Jellyfin.Plugin.ApiKeyMiddleware -c Release
```

Copy `Jellyfin.Plugin.ApiKeyMiddleware.dll` into `<Jellyfin data>/plugins/ApiKeyMiddleware_<version>/` and restart Jellyfin.

### Releasing

Versions follow Jellyfin: `<jellyfin major.minor.patch>.<plugin revision>`, e.g. `12.4.0.0`, then `12.4.0.1` for a plugin fix; a new Jellyfin version means updating the package references and starting at `.0` again.

1. Set `AssemblyVersion`/`FileVersion` (and, for a new Jellyfin version, the `Jellyfin.Controller`/`Jellyfin.Model` versions) in the `.csproj`.
2. `.\tools\New-ReleasePackage.ps1 -Changelog '...'` builds `artifacts/api-key-middleware_<version>.zip` and adds the version (with the zip's MD5 and the matching `targetAbi`) to `manifest.json`.
3. Commit and push `manifest.json`, then upload that exact zip to the GitHub release named in the printed `gh release create` command.

### Development deploy script

`tools/Deploy-Plugin.ps1` builds the plugin and copies it to a Windows Jellyfin server over PowerShell remoting (`-Server`). With `-Restart` it stops Jellyfin (not while something is playing), copies the DLL, removes older plugin folders and starts Jellyfin again (scheduled task or Windows service). For that it uses a dedicated `deploy` API key, read on the server from `-ApiKeyFile` and never printed. Machine-specific values can be kept in `tools/deploy.settings.psd1` (git-ignored; start from `deploy.settings.example.psd1`). If the deploy key is scoped by this plugin, its section is:

```
[deploy] enforce
GET /Sessions
POST /System/Shutdown
GET /Plugins
```

## License

[GPL-3.0](LICENSE), the license of the Jellyfin packages the plugin builds on.
