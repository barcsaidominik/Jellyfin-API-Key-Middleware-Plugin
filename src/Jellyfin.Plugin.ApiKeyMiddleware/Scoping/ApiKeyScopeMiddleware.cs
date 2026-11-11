using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        var decision = _rules.Get(rulesText).Decide(keyName, method, path);
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
}
