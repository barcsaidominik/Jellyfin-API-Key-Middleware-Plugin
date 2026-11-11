using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

// Inserted in front of Jellyfin's whole pipeline (before authentication and routing). Only requests
// authenticated with an API key are checked; user sessions and anonymous requests pass untouched.
// If the plugin is not loaded the middleware is absent and every key has full access again (fail-open).
internal sealed class ApiKeyScopeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<ApiKeyScopeMiddleware>();
            next(app);
        };
}

internal sealed class ApiKeyScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IScopeSettings _settings;
    private readonly RulesCache _rules;
    private readonly ObservedRequests _observed;
    private readonly ILogger<ApiKeyScopeMiddleware> _logger;

    public ApiKeyScopeMiddleware(RequestDelegate next, IScopeSettings settings, RulesCache rules, ObservedRequests observed, ILogger<ApiKeyScopeMiddleware> logger)
    {
        _next = next;
        _settings = settings;
        _rules = rules;
        _observed = observed;
        _logger = logger;
    }

    // IAuthorizationContext is resolved per request: its lifetime is Jellyfin's business.
    public async Task InvokeAsync(HttpContext context, IAuthorizationContext authorizationContext)
    {
        var rulesText = _settings.Rules;
        var path = RequestPath.Relative(context.Request.Path.Value, _settings.BaseUrl);
        if (rulesText is null || path is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Same parsing (header, legacy headers, ApiKey/api_key query) and lookup Jellyfin uses itself.
        var auth = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
        if (!auth.IsApiKey)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // For API keys Jellyfin sets Client to the key's name (AuthorizationContext), whatever the caller sent.
        var keyName = auth.Client ?? string.Empty;
        var method = context.Request.Method;

        // Jellyfin's WebSocketHandlerMiddleware accepts an upgrade on any path, and the messages on the
        // socket are not HTTP requests. So in enforce mode an upgrade is only allowed on Jellyfin's own
        // WebSocket path (/socket), and only if a GET rule allows that path. HTTP/2 upgrades arrive as
        // CONNECT and are matched like GET.
        var webSocket = IsWebSocketRequest(context);
        if (webSocket)
        {
            method = HttpMethods.Get;
        }

        var decision = _rules.Get(rulesText).Decide(keyName, method, path);
        if (webSocket && decision == ScopeDecision.Allowed && !IsWebSocketPath(path))
        {
            decision = ScopeDecision.Denied;
            method = WebSocketMethodLabel;
        }
        if (decision == ScopeDecision.Pass)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var denied = decision == ScopeDecision.Denied;
        var template = RequestPath.Template(path);
        var isNew = _observed.Record(keyName, method, template, denied);
        if (denied)
        {
            var level = isNew ? LogLevel.Warning : LogLevel.Debug;
            if (_logger.IsEnabled(level))
            {
                _logger.Log(level, "API key {KeyName} denied: {Method} {Template}", keyName, method, template);
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (isNew && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("API key {KeyName} {Decision}: {Method} {Template}", keyName, decision, method, template);
        }

        await _next(context).ConfigureAwait(false);
    }

    // Label in the log and the observed list for an upgrade denied because of its path.
    internal const string WebSocketMethodLabel = "WEBSOCKET";

    private const string WebSocketPath = "socket";

    // Deliberately broader than ASP.NET Core's own check (WebSocketMiddleware), so nothing it would
    // accept as a WebSocket request is treated as a plain request here.
    internal static bool IsWebSocketRequest(HttpContext context)
    {
        if (context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true)
        {
            return true;
        }

        foreach (var value in context.Request.Headers.Upgrade)
        {
            if (value is not null && value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWebSocketPath(string[] path)
        => path.Length == 1 && string.Equals(path[0], WebSocketPath, StringComparison.OrdinalIgnoreCase);
}
