using Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Tests;

public class ScopeRulesTests
{
    private const string Sample = """
        # homepage widget
        [homepage] enforce
        GET /Sessions
        GET /Items/Counts
        GET /Users/*/Items/Latest   # trailing comment
        [watchstate] audit
        [scripts] enforce
        * /Library/**
        [*] enforce
        GET /System/Info/Public
        """;

    private static string[] P(string path) => RequestPath.Split(path);

    [Theory]
    [InlineData("homepage", "GET", "/Sessions", ScopeDecision.Allowed)]
    [InlineData("HomePage", "get", "/sessions", ScopeDecision.Allowed)]
    [InlineData("homepage", "HEAD", "/Sessions", ScopeDecision.Allowed)]
    [InlineData("homepage", "POST", "/Sessions", ScopeDecision.Denied)]
    [InlineData("homepage", "GET", "/Sessions/Logout", ScopeDecision.Denied)]
    [InlineData("homepage", "GET", "/Users/abc/Items/Latest", ScopeDecision.Allowed)]
    [InlineData("homepage", "GET", "/Users/Items/Latest", ScopeDecision.Denied)]
    [InlineData("homepage", "DELETE", "/Items/abc", ScopeDecision.Denied)]
    [InlineData("watchstate", "DELETE", "/Items/abc", ScopeDecision.Audited)]
    [InlineData("scripts", "POST", "/Library/Refresh", ScopeDecision.Allowed)]
    [InlineData("scripts", "GET", "/Library", ScopeDecision.Allowed)]
    [InlineData("scripts", "GET", "/LibraryX", ScopeDecision.Denied)]
    [InlineData("unknown", "GET", "/System/Info/Public", ScopeDecision.Allowed)]
    [InlineData("unknown", "GET", "/System/Info", ScopeDecision.Denied)]
    public void Decide(string key, string method, string path, ScopeDecision expected)
    {
        var rules = ScopeRules.Parse(Sample);

        Assert.Empty(rules.Errors);
        Assert.Equal(expected, rules.Decide(key, method, P(path)));
    }

    [Fact]
    public void KeysWithoutSectionAndWithoutDefault_AreAudited()
        => Assert.Equal(ScopeDecision.Audited, ScopeRules.Parse("[homepage] enforce").Decide("other", "DELETE", P("/Items/x")));

    [Fact]
    public void AllowMode_Passes()
        => Assert.Equal(ScopeDecision.Pass, ScopeRules.Parse("[k] allow").Decide("k", "DELETE", P("/Items/x")));

    [Fact]
    public void EmptyText_AuditsEverything()
    {
        var rules = ScopeRules.Parse(null);

        Assert.Empty(rules.Errors);
        Assert.Equal(ScopeDecision.Audited, rules.Decide("k", "GET", P("/x")));
    }

    [Theory]
    [InlineData("[k]")]
    [InlineData("[k] enforcee")]
    [InlineData("[k] 1")]
    [InlineData("[k] audit,allow")]
    public void MissingOrUnknownMode_FailsClosed(string header)
    {
        var rules = ScopeRules.Parse(header + "\nGET /Sessions");

        Assert.Single(rules.Errors);
        Assert.Equal(ScopeMode.Enforce, rules.ScopeFor("k").Mode);
        Assert.Equal(ScopeDecision.Allowed, rules.Decide("k", "GET", P("/Sessions")));
        Assert.Equal(ScopeDecision.Denied, rules.Decide("k", "GET", P("/Items")));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("GET Sessions")]
    [InlineData("FETCH /Sessions")]
    [InlineData("GET /Sessions?x=1")]
    [InlineData("GET /a/**/b")]
    [InlineData("GET /a b")]
    public void InvalidRule_IsReportedAndDropped(string rule)
    {
        var rules = ScopeRules.Parse("[k] enforce\n" + rule);

        Assert.Single(rules.Errors);
        Assert.StartsWith("line 2:", rules.Errors[0], StringComparison.Ordinal);
        Assert.Empty(rules.ScopeFor("k").Rules);
    }

    [Fact]
    public void RuleOutsideSection_IsReported()
        => Assert.Single(ScopeRules.Parse("GET /Sessions\n[k] audit").Errors);

    [Fact]
    public void DuplicateSection_LaterWins()
    {
        var rules = ScopeRules.Parse("[k] audit\n[K] enforce\nGET /a");

        Assert.Single(rules.Errors);
        Assert.Equal(ScopeMode.Enforce, rules.ScopeFor("k").Mode);
    }

    [Fact]
    public void KeyNameIsTrimmed()
        => Assert.Equal(ScopeMode.Allow, ScopeRules.Parse("[ my key ] allow").ScopeFor("my key ").Mode);

    [Fact]
    public void WindowsLineEndings_AreAccepted()
    {
        var rules = ScopeRules.Parse("[k] enforce\r\nGET /a\r\n");

        Assert.Empty(rules.Errors);
        Assert.Equal(ScopeDecision.Allowed, rules.Decide("k", "GET", P("/a")));
    }

    [Fact]
    public void RootGlobstar_MatchesEverything()
        => Assert.Equal(ScopeDecision.Allowed, ScopeRules.Parse("[k] enforce\n* /**").Decide("k", "PUT", P("/any/thing")));

    // Path edge cases: the matcher must not see an allowed path where routing sees another endpoint.
    [Theory]
    [InlineData("GET", "/Sessions/", ScopeDecision.Allowed)] // trailing slash: same route
    [InlineData("GET", "//Sessions", ScopeDecision.Allowed)] // empty segments are ignored
    [InlineData("HEAD", "/Users/abc/Items/Latest", ScopeDecision.Allowed)] // HEAD served by the GET handler
    [InlineData("OPTIONS", "/Sessions", ScopeDecision.Denied)] // GET does not cover other methods
    [InlineData("GET", "/Users/a%2Fb/Items/Latest", ScopeDecision.Allowed)] // %2F stays one segment, as in routing
    [InlineData("GET", "/Users/a/b/Items/Latest", ScopeDecision.Denied)] // a real slash does not
    [InlineData("GET", "/Sessions/x", ScopeDecision.Denied)] // no implicit prefix match
    [InlineData("GET", "/", ScopeDecision.Denied)]
    public void PathEdgeCases(string method, string path, ScopeDecision expected)
        => Assert.Equal(expected, ScopeRules.Parse(Sample).Decide("homepage", method, P(path)));
}

public class RequestPathTests
{
    [Theory]
    [InlineData("/Sessions", null, "Sessions")]
    [InlineData("/Sessions/", "", "Sessions")]
    [InlineData("/jellyfin/Sessions", "/jellyfin", "Sessions")]
    [InlineData("/JellyFin/Sessions", "jellyfin/", "Sessions")]
    [InlineData("//Sessions//x", null, "Sessions/x")]
    public void Relative_StripsBaseUrl(string path, string? baseUrl, string expected)
        => Assert.Equal(expected, string.Join('/', RequestPath.Relative(path, baseUrl)!));

    [Theory]
    [InlineData("/Sessions", "/jellyfin")]
    [InlineData("/jellyfinx/Sessions", "/jellyfin")]
    public void Relative_OutsideBaseUrl_IsNull(string path, string baseUrl)
        => Assert.Null(RequestPath.Relative(path, baseUrl));

    [Theory]
    [InlineData("/Items/0b7e2c6f4a1d4e8f9c3b2a1d0e9f8c7b/Images/Primary", "/Items/*/Images/Primary")]
    [InlineData("/Users/0b7e2c6f-4a1d-4e8f-9c3b-2a1d0e9f8c7b/Items", "/Users/*/Items")]
    [InlineData("/Items/42/PlaybackInfo", "/Items/*/PlaybackInfo")]
    [InlineData("/System/Info/Public", "/System/Info/Public")]
    [InlineData("/Videos/abcdef", "/Videos/abcdef")]
    [InlineData("", "/")]
    [InlineData("/Items/a\nFAKE LOG LINE\r/x", "/Items/a_FAKE LOG LINE_/x")]
    public void Template_ReplacesIds(string path, string expected)
        => Assert.Equal(expected, RequestPath.Template(RequestPath.Split(path)));
}
