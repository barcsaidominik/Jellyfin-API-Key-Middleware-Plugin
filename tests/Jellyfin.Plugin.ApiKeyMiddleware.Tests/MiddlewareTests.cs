using Jellyfin.Plugin.ApiKeyMiddleware.Scoping;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
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

    private static async Task<(bool NextCalled, int Status, ObservedRequests Observed, FakeAuthorizationContext Auth)> Run(
        AuthorizationInfo info, string method, string path, string? rules = Rules, string? baseUrl = null)
    {
        var nextCalled = false;
        var observed = new ObservedRequests(new FakeTimeProvider());
        var middleware = new ApiKeyScopeMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new FakeSettings(rules, baseUrl),
            new RulesCache(),
            observed,
            NullLogger<ApiKeyScopeMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
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
