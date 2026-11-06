# Security policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately through GitHub: **Security → Report a vulnerability** on this repository. Do not open a public issue for them. Include the Jellyfin and plugin versions, the rules involved and the request that got through (method, path, how the key was sent). Never include real API keys.

## Supported versions

Only the latest plugin release is supported. Each release is built and tested against one Jellyfin version, which is the first three parts of the plugin version (plugin `12.4.0.x` → Jellyfin 12.4.0).

## What the plugin does and does not protect

The plugin limits which endpoints a Jellyfin **API key** can call (HTTP method + path). In scope:

- an API-key request reaching an endpoint its section does not allow in `enforce` mode;
- a way to send an API key that Jellyfin accepts but the plugin does not recognise;
- leaking tokens through the plugin's logs, pages or endpoints.

Out of scope, by design (see the README's *Known limitations*):

- **Fail-open:** when the plugin is not loaded (disabled, incompatible Jellyfin version, startup failure), API keys have Jellyfin's normal full access.
- Keys in `allow` or `audit` mode are still full administrators.
- User sessions and anonymous requests are not affected; the plugin is not a replacement for Jellyfin's own authentication, keeping Jellyfin up to date, a firewall or a VPN.
