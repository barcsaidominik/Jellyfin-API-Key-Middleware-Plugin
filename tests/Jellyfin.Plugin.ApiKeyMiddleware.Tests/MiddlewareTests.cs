using Jellyfin.Plugin.ApiKeyMiddleware.Scoping;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Tests;

public class ApiKeyScopeMiddlewareTests
{
    private const string Rules = """
        [homepage] enforce
        GET /Sessions
        [watchstate] audit
        [trusted] allow
        """;

    private sealed class FakeSettings(string? rules, string? baseUrl = null) : IScopeSettings
    {
        public string? Rules => rules;

        public string? BaseUrl => baseUrl;
    }

    private sealed class FakeAuthorizationContext(AuthorizationInfo info) : IAuthorizationContext
    {
        public int Calls { get; private set; }

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext)
        {
            Calls++;
            return Task.FromResult(info);
        }

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext) => GetAuthorizationInfo(requestContext.HttpContext);
    }

    private static AuthorizationInfo ApiKey(string name) => new() { IsApiKey = true, IsAuthenticated = true, Client = name };

    private sealed class ThrowingAuthorizationContext : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext) => throw new InvalidOperationException("database unavailable");

        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext) => throw new InvalidOperationException("database unavailable");
    }

    private sealed class CapturingLogger : ILogger<ApiKeyScopeMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<(bool NextCalled, int Status, ObservedRequests Observed, FakeAuthorizationContext Auth)> Run(
        AuthorizationInfo info,
        string method,
        string path,
        string? rules = Rules,
        string? baseUrl = null,
        ObservedRequests? observed = null,
        ILogger<ApiKeyScopeMiddleware>? logger = null,
        Action<HttpRequest>? configure = null)
    {
        var nextCalled = false;
        observed ??= new ObservedRequests(new FakeTimeProvider());
        var middleware = new ApiKeyScopeMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FakeSettings(rules, baseUrl),
            new RulesCache(),
            observed,
            logger ?? NullLogger<ApiKeyScopeMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        configure?.Invoke(context.Request);
        var auth = new FakeAuthorizationContext(info);
        await middleware.InvokeAsync(context, auth);
        return (nextCalled, context.Response.StatusCode, observed, auth);
    }

    [Fact]
    public async Task EnforcedKey_MatchingRule_PassesAndIsRecorded()
    {
        var result = await Run(ApiKey("homepage"), "GET", "/Sessions");

        Assert.True(result.NextCalled);
        var entry = Assert.Single(result.Observed.Snapshot());
        Assert.Equal(("homepage", "GET", "/Sessions", 1L, 0L), (entry.KeyName, entry.Method, entry.Template, entry.Count, entry.Denied));
    }

    [Fact]
    public async Task EnforcedKey_NoMatchingRule_Is403()
    {
        var result = await Run(ApiKey("homepage"), "POST", "/System/Shutdown");

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Equal(1L, Assert.Single(result.Observed.Snapshot()).Denied);
    }

    [Fact]
    public async Task AuditedKey_PassesAndIsRecordedWithTemplate()
    {
        var result = await Run(ApiKey("watchstate"), "DELETE", "/Items/0b7e2c6f4a1d4e8f9c3b2a1d0e9f8c7b");

        Assert.True(result.NextCalled);
        Assert.Equal("/Items/*", Assert.Single(result.Observed.Snapshot()).Template);
    }

    [Fact]
    public async Task AllowedKey_PassesUnrecorded()
    {
        var result = await Run(ApiKey("trusted"), "POST", "/System/Shutdown");

        Assert.True(result.NextCalled);
        Assert.Empty(result.Observed.Snapshot());
    }

    [Fact]
    public async Task UserSession_IsNotScoped()
    {
        var result = await Run(new AuthorizationInfo { IsAuthenticated = true, Client = "homepage" }, "POST", "/System/Shutdown");

        Assert.True(result.NextCalled);
        Assert.Empty(result.Observed.Snapshot());
    }

    [Fact]
    public async Task PluginNotInitialised_PassesWithoutAuthLookup()
    {
        var result = await Run(ApiKey("homepage"), "POST", "/System/Shutdown", rules: null);

        Assert.True(result.NextCalled);
        Assert.Equal(0, result.Auth.Calls);
    }

    [Fact]
    public async Task BaseUrl_IsStrippedBeforeMatching()
    {
        var allowed = await Run(ApiKey("homepage"), "GET", "/jellyfin/Sessions", baseUrl: "/jellyfin");
        var denied = await Run(ApiKey("homepage"), "GET", "/jellyfin/jellyfin/Sessions", baseUrl: "/jellyfin");

        Assert.True(allowed.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Status);
    }

    [Fact]
    public async Task OutsideBaseUrl_PassesWithoutAuthLookup()
    {
        var result = await Run(ApiKey("homepage"), "POST", "/System/Shutdown", baseUrl: "/jellyfin");

        Assert.True(result.NextCalled);
        Assert.Equal(0, result.Auth.Calls);
    }

    // No token, or a token Jellyfin does not know: not an API key, so the plugin passes it on and
    // Jellyfin's own authentication decides (401 for protected endpoints).
    [Fact]
    public async Task AnonymousOrUnknownToken_IsNotScoped()
    {
        var result = await Run(new AuthorizationInfo { IsAuthenticated = false, IsApiKey = false }, "POST", "/System/Shutdown");

        Assert.True(result.NextCalled);
        Assert.Empty(result.Observed.Snapshot());
    }

    // The key lookup failing must not let the request through.
    [Fact]
    public async Task AuthLookupFailure_PropagatesAndDoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new ApiKeyScopeMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FakeSettings(Rules),
            new RulesCache(),
            new ObservedRequests(new FakeTimeProvider()),
            NullLogger<ApiKeyScopeMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/Sessions";

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, new ThrowingAuthorizationContext()));
        Assert.False(nextCalled);
    }

    // Empty rules text (e.g. a cleared text field): no sections, so every key is audited = full access.
    [Fact]
    public async Task EmptyRules_AuditsEveryKey()
    {
        var result = await Run(ApiKey("homepage"), "POST", "/System/Shutdown", rules: string.Empty);

        Assert.True(result.NextCalled);
        Assert.Equal(0L, Assert.Single(result.Observed.Snapshot()).Denied);
    }

    private static void WebSocketUpgrade(HttpRequest request)
    {
        request.Headers.Connection = "Upgrade";
        request.Headers.Upgrade = "websocket";
    }

    private sealed class ExtendedConnect : IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => true;

        public string? Protocol => "websocket";

        public ValueTask<Stream> AcceptAsync() => throw new NotSupportedException();
    }

    private const string SocketRules = """
        [homepage] enforce
        GET /Sessions
        [socket] enforce
        GET /socket
        [watchstate] audit
        """;

    // Jellyfin's WebSocketHandlerMiddleware accepts an upgrade on any path, so an allowed GET path
    // must not open a WebSocket: only /socket with its own rule can.
    [Theory]
    [InlineData("homepage", "/Sessions", false)] // GET /Sessions allowed, upgrade on it is not
    [InlineData("homepage", "/socket", false)] // no GET /socket rule
    [InlineData("socket", "/socket", true)]
    [InlineData("socket", "/Socket/", true)]
    [InlineData("watchstate", "/Sessions", true)] // audit: everything allowed
    public async Task WebSocketUpgrade_OnlyOnSocketPathWithRule(string key, string path, bool allowed)
    {
        var result = await Run(ApiKey(key), "GET", path, rules: SocketRules, configure: WebSocketUpgrade);

        Assert.Equal(allowed, result.NextCalled);
        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task WebSocketUpgrade_DeniedForItsPath_IsRecordedAsWebSocket()
    {
        var result = await Run(ApiKey("homepage"), "GET", "/Sessions", rules: SocketRules, configure: WebSocketUpgrade);

        var entry = Assert.Single(result.Observed.Snapshot());
        Assert.Equal((ApiKeyScopeMiddleware.WebSocketMethodLabel, "/Sessions", 1L), (entry.Method, entry.Template, entry.Denied));
    }

    // HTTP/2 WebSockets arrive as CONNECT (RFC 8441) and are matched like GET.
    [Theory]
    [InlineData("homepage", "/Sessions", false)]
    [InlineData("socket", "/socket", true)]
    public async Task Http2WebSocket_IsTreatedLikeAnUpgrade(string key, string path, bool allowed)
    {
        var result = await Run(ApiKey(key), "CONNECT", path, rules: SocketRules, configure: request => request.HttpContext.Features.Set<IHttpExtendedConnectFeature>(new ExtendedConnect()));

        Assert.Equal(allowed, result.NextCalled);
    }

    [Theory]
    [InlineData("Upgrade", "WebSocket")]
    [InlineData("Upgrade", "h2c, websocket")]
    public void IsWebSocketRequest_RecognisesUpgradeHeader(string connection, string upgrade)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Connection = connection;
        context.Request.Headers.Upgrade = upgrade;

        Assert.True(ApiKeyScopeMiddleware.IsWebSocketRequest(context));
    }

    [Fact]
    public void IsWebSocketRequest_PlainRequest_IsFalse()
        => Assert.False(ApiKeyScopeMiddleware.IsWebSocketRequest(new DefaultHttpContext()));

    // Once the observed list is full, new combinations are not recorded: denials are still enforced,
    // but no longer logged as Warning (only Debug).
    [Fact]
    public async Task DenialAfterObservedListIsFull_IsStill403_ButNotLoggedAsWarning()
    {
        var observed = new ObservedRequests(new FakeTimeProvider());
        for (var i = 0; i < ObservedRequests.Capacity; i++)
        {
            observed.Record("filler", "GET", "/p" + i, denied: false);
        }

        var logger = new CapturingLogger();
        var result = await Run(ApiKey("homepage"), "POST", "/System/Shutdown", observed: observed, logger: logger);

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.NextCalled);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("denied", StringComparison.Ordinal));
    }
}

public class ObservedRequestsTests
{
    [Fact]
    public void Record_ReportsFirstSightingAndCounts()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero));
        var observed = new ObservedRequests(time);

        Assert.True(observed.Record("k", "get", "/a", denied: false));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(observed.Record("k", "GET", "/a", denied: true));

        var entry = Assert.Single(observed.Snapshot());
        Assert.Equal(("GET", 2L, 1L), (entry.Method, entry.Count, entry.Denied));
        Assert.Equal(time.GetUtcNow(), entry.LastSeen);
    }

    [Fact]
    public void Record_StopsAddingAtCapacity()
    {
        var observed = new ObservedRequests(new FakeTimeProvider());
        for (var i = 0; i < ObservedRequests.Capacity + 10; i++)
        {
            observed.Record("k", "GET", "/p" + i, denied: false);
        }

        Assert.Equal(ObservedRequests.Capacity, observed.Snapshot().Count);
    }
}
