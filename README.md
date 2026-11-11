# Jellyfin API Key Middleware

A Jellyfin plugin that adds a per-API-key allowlist (HTTP method + path). In Jellyfin every API key is a full administrator. If a key leaks (for example from a dashboard's configuration), in enforce mode the plugin only lets it reach the endpoints the application behind it actually needs.

Early release: built and tested against Jellyfin **12.4.0**. It narrows what API keys can do; it is not a replacement for Jellyfin's own authentication, keeping Jellyfin up to date, a firewall or a VPN. Feedback, especially on bypasses and route edge cases, is welcome (see [SECURITY.md](SECURITY.md)).

**AI disclosure:** this plugin was developed primarily with an LLM (Claude, via Claude Code): most of the code, tests, documentation and the security review were written with it. The maintainer directed the work, reviewed the changes and tested every release on a live Jellyfin server. There has been no independent security audit. See Jellyfin's [LLM policies](https://jellyfin.org/docs/general/contributing/llm-policies/).

## How it works

- The plugin registers an `IStartupFilter` that puts a middleware at the **front** of Jellyfin's request pipeline (before authentication and routing). Plugin services are added to the host's own DI container before the web host is built (Jellyfin 12.4: `Program.cs` → `ApplicationHost.Init` → `RegisterServices`).
- Requests are identified with the same `IAuthorizationContext` Jellyfin uses itself: the `Authorization`/`X-Emby-Authorization` header and the `ApiKey` query parameter (plus the legacy `X-Emby-Token`/`X-MediaBrowser-Token` headers and `api_key` query parameter if the server still accepts them; Jellyfin 12.4 rejects them by default). Whatever way Jellyfin accepts a key, the plugin sees the same key. For API keys Jellyfin sets the `Client` field to the **key's name**, whatever the caller sends, so rules refer to key names.
- Only requests made with an API key are checked. User sessions and anonymous requests pass through unchanged.
- A denied request gets **403** with an empty body. Every new (key, method, path template) combination is logged once (Warning when denied, Information otherwise) while the observed list has room (see below). Tokens and query strings are never logged.

## Security boundary

```
client ──► [plugin middleware] ──► BaseUrl redirect ──► auth ──► routing ──► authorization ──► WebSocket handler / endpoint
             sees: method, raw path, the key's name
             does not see: messages on an open WebSocket, which endpoint routing picks
```

When the plugin lets a request through without checking it:

| Situation | Result |
|---|---|
| Plugin not loaded or not initialised (disabled, incompatible Jellyfin version, startup failure) | **fail-open**: every key is a full administrator |
| Request without an API key (user session, anonymous, unknown token) | not checked; Jellyfin's own authentication decides |
| Key in `allow` or `audit` mode, or a key without a section when there is no `[*]` section | allowed (full administrator) |
| Rules text empty, or reset to the factory default and no backup to restore | every key audited = allowed |
| Path outside Jellyfin's `BaseUrl` | not checked; Jellyfin does not serve the API there (it redirects) |
| Key in `enforce` mode, no rule matches | **403** |
| Key in `enforce` mode, WebSocket upgrade on a path other than `/socket`, or without a `GET /socket` rule | **403** |
| Invalid section header or rule line | fail-closed: the header becomes `enforce`, the line is dropped |
| Looking up the key fails (exception) | the request fails; it is not let through |

## Design decisions and validation

- **Why a plugin:** Jellyfin has no per-key permissions (every API key is an administrator), and changing that needs a change in Jellyfin itself. A plugin can be installed today, at the price of the fail-open behaviour above.
- **Why in front of the pipeline:** the middleware sees the request before Jellyfin's `BaseUrl` mapping, so it strips the `BaseUrl` itself and matches the same decoded path that routing later uses. It asks Jellyfin's own `IAuthorizationContext` for the key, so it recognises a key exactly the way Jellyfin does.
- **Path rules, not endpoints:** rules match method + path patterns, not the endpoint routing finally selects. This keeps rules readable and independent of routing internals, but a `*` can match more than the route it was built from (see *Known limitations*).
- **Validation:** unit tests for the rule parser, path handling, the middleware decision, the rule builder and the backup (`dotnet test`, also run in CI). Each release was smoke-tested by hand on one live Jellyfin 12.4.0 server (allowed call → 200, disallowed call → 403, with the key sent in the `Authorization` header and in the `ApiKey` query parameter). There are no automated integration tests against a running Jellyfin and there has been no independent security audit.

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
- **WebSockets:** in `enforce` mode a WebSocket upgrade is allowed only on `/socket` (the path Jellyfin's clients use) and only with a `GET /socket` rule; an HTTP/2 WebSocket (`CONNECT`) is matched the same way. An upgrade on any other path gets 403, even if a `GET` rule allows that path, and shows up as `WEBSOCKET <path>` in the observed list. (Jellyfin itself would accept an upgrade on any path.)
- **Default:** keys without a section get the `[*]` section, or `audit` if there is none. Factory setting: `[*] audit`.
- **Errors fail closed:** a missing or unknown mode becomes `enforce`, an invalid rule line is dropped (so it allows nothing). Errors are listed on the configuration page.
- Jellyfin's `BaseUrl` is stripped from the path; do not include it in rules.

### Building an allowlist

1. Leave new keys in `audit` mode (the factory setting).
2. Use the applications for a while.
3. Open the rule builder, pick the key: what it called is pre-selected. Review the list (drop anything the application should not need), switch the mode to `enforce`, apply and save.
4. Watch for denials: a denied request is logged as a Warning and shows up as *denied* in the builder and in the observed list.

The observed requests are kept in memory only (cleared on restart, at most 2000 entries; once full, new combinations are no longer recorded, but rules are still enforced). The *Observed requests* section of the page shows them in rule format as well.

## Known limitations

- **Fail-open:** if the plugin is not loaded (disabled, wrong version, after a Jellyfin upgrade), the middleware is absent and every key has full access again. After upgrading Jellyfin, check that the plugin is Active.
- **Unreadable configuration:** if Jellyfin cannot read the plugin's configuration file (e.g. broken XML after a manual edit), it replaces it with the factory default `[*] audit`, which gives every key full access. The plugin keeps a copy of the rules (updated at startup and on every save) in `Jellyfin.Plugin.ApiKeyMiddleware.rules.txt` next to that file and restores them at startup in this case, logging a Warning. If both files are lost, the factory default applies.
- **Admin above the rules:** a key in `allow` or `audit` mode can rewrite the plugin's own settings. In enforce mode only the allowed endpoints are reachable, so the settings are not.
- **WebSocket messages are not checked:** once a key may open a WebSocket (`GET /socket`, see *Rules*), the plugin does not see the messages on it. Over it Jellyfin streams what an administrator would get to API keys: all sessions, the activity log and scheduled task information. Allow `GET /socket` only for applications that need it.
- **`*` matches literal segments too:** a rule built from `POST /Users/{userId}` is `POST /Users/*`, which also matches `POST /Users/New` (create a user, admin-only), because routing prefers the literal route. Likewise `GET /Users/*` matches `GET /Users/Me`, and `GET /Items/*` matches `GET /Items/Counts`. Rules cannot exclude paths. The builder marks such rules with **also matches admin** (hover for the list) and warns when the selected rules reach admin-only endpoints that are not ticked; it does not check custom rules.
- **Observed list full:** after 2000 (key, method, path template) combinations, new ones are not recorded, and a new denial is logged at Debug level only, not as a Warning. Rules are still enforced. A key that can send many different paths can fill the list; restarting Jellyfin clears it.
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
