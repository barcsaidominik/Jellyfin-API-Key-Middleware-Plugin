using Jellyfin.Plugin.ApiKeyMiddleware.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using System.Reflection;
using System.Xml.Linq;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Tests;

public class EndpointCatalogTests
{
    private static RouteEndpoint Route(string template, string[] methods, params object[] metadata)
        => new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            new EndpointMetadataCollection([new HttpMethodMetadata(methods), .. metadata]),
            template);

    private static IReadOnlyList<CatalogEndpoint> Build(params Endpoint[] endpoints)
        => EndpointCatalog.Build(endpoints, _ => null);

    [Theory]
    [InlineData("Users/{userId}/Items/Latest", "/Users/*/Items/Latest")]
    [InlineData("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}", "/Videos/*/hls1/*/*")]
    [InlineData("Items/{itemId:guid}", "/Items/*")]
    [InlineData("web/{**path}", "/web/**")]
    [InlineData("System/Info/Public", "/System/Info/Public")]
    [InlineData("Videos/{itemId}/stream.mp4", "/Videos/*/stream.mp4")]
    public void RulePaths_TranslatesTemplates(string template, string expected)
        => Assert.Equal([expected], EndpointCatalog.RulePaths(RoutePatternFactory.Parse(template)));

    [Fact]
    public void RulePaths_TrailingOptionalParameter_GivesBothRules()
        => Assert.Equal(["/Items", "/Items/*"], EndpointCatalog.RulePaths(RoutePatternFactory.Parse("Items/{itemId?}")));

    [Fact]
    public void Access_FromAuthorizationMetadata()
    {
        var rows = Build(
            Route("System/Info/Public", ["GET"]),
            Route("Items/Counts", ["GET"], new AuthorizeAttribute()),
            Route("System/Shutdown", ["POST"], new AuthorizeAttribute { Policy = "RequiresElevation" }),
            Route("Startup/User", ["GET"], new AuthorizeAttribute { Policy = "FirstTimeSetupOrElevated" }),
            Route("Auth/Providers", ["GET"], new AuthorizeAttribute(), new AllowAnonymousAttribute()));

        Assert.Equal(EndpointAccess.Anonymous, rows.Single(r => r.Rule == "/System/Info/Public").Access);
        Assert.Equal(EndpointAccess.User, rows.Single(r => r.Rule == "/Items/Counts").Access);
        Assert.Equal(["Default"], rows.Single(r => r.Rule == "/Items/Counts").Policies);
        Assert.Equal(EndpointAccess.Admin, rows.Single(r => r.Rule == "/System/Shutdown").Access);
        Assert.Equal(EndpointAccess.Admin, rows.Single(r => r.Rule == "/Startup/User").Access);
        Assert.Equal(EndpointAccess.Anonymous, rows.Single(r => r.Rule == "/Auth/Providers").Access);
    }

    [Fact]
    public void HiddenAndObsolete_AreFlagged()
    {
        var row = Build(Route("Users/{userId}/Items/Latest", ["GET"], new AuthorizeAttribute(), new ApiExplorerSettingsAttribute { IgnoreApi = true }, new ObsoleteAttribute())).Single();

        Assert.True(row.Hidden);
        Assert.True(row.Obsolete);
    }

    [Fact]
    public void SameRule_IsMerged_WithTheStrictestAccess()
    {
        var row = Build(
            Route("Items/{itemId}", ["GET"], new AuthorizeAttribute()),
            Route("Items/{id:guid}", ["GET"], new AuthorizeAttribute { Policy = "RequiresElevation" }, new ApiExplorerSettingsAttribute { IgnoreApi = true })).Single();

        Assert.Equal("/Items/*", row.Rule);
        Assert.Equal(["/Items/{itemId}", "/Items/{id:guid}"], row.Templates);
        Assert.Equal(EndpointAccess.Admin, row.Access);
        Assert.False(row.Hidden);
    }

    [Fact]
    public void HeadNextToGet_IsDropped_HeadAloneIsKept()
    {
        var rows = Build(
            Route("Videos/{itemId}/master.m3u8", ["GET", "HEAD"]),
            Route("Audio/{itemId}/universal", ["HEAD"]));

        Assert.Equal(
            ["HEAD /Audio/*/universal", "GET /Videos/*/master.m3u8"],
            rows.Select(r => $"{r.Method} {r.Rule}"));
    }

    [Fact]
    public void EndpointsWithoutHttpMethods_AreSkipped()
    {
        var noMethods = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("fallback"), 0, EndpointMetadataCollection.Empty, "fallback");

        Assert.Empty(EndpointCatalog.Build([noMethods], _ => null));
    }
}

public class XmlDocSummariesTests
{
    // Members are only inspected through reflection.
#pragma warning disable CA1822, IDE0060
    private sealed class Sample
    {
        public void NoArgs()
        {
        }

        public void Args(Guid? id, Guid[] ids, IReadOnlyList<string> names, CancellationToken token, ref int count)
        {
        }

        public void Overload(int a)
        {
        }

        public void Overload(string a)
        {
        }
    }
#pragma warning restore CA1822, IDE0060

    private static MethodInfo Method(string name, params Type[] parameters)
        => parameters.Length == 0 && name != nameof(Sample.NoArgs)
            ? typeof(Sample).GetMethod(name)!
            : typeof(Sample).GetMethod(name, parameters)!;

    private static readonly string TypeId = typeof(Sample).FullName!.Replace('+', '.');

    [Fact]
    public void MemberId_MatchesTheCompilerFormat()
    {
        Assert.Equal($"M:{TypeId}.NoArgs", XmlDocSummaries.MemberId(typeof(Sample).GetMethod(nameof(Sample.NoArgs))!));
        Assert.Equal(
            $"M:{TypeId}.Args(System.Nullable{{System.Guid}},System.Guid[],System.Collections.Generic.IReadOnlyList{{System.String}},System.Threading.CancellationToken,System.Int32@)",
            XmlDocSummaries.MemberId(Method(nameof(Sample.Args))));
    }

    [Fact]
    public void Parse_RendersReferencesAndCollapsesWhitespace()
    {
        var docs = XmlDocSummaries.Parse(XDocument.Parse($"""
            <doc><members>
              <member name="M:{TypeId}.NoArgs">
                <summary>
                  Gets the <see cref="T:Jellyfin.Data.Entities.User"/> list, or <see langword="null"/>.
                </summary>
              </member>
              <member name="T:{TypeId}"><summary>Not a method.</summary></member>
            </members></doc>
            """));

        Assert.Equal("Gets the User list, or null.", docs[$"M:{TypeId}.NoArgs"]);
        Assert.Single(docs);
    }

    [Fact]
    public void Lookup_FallsBackToTheOnlyMemberWithThatName()
    {
        var docs = new Dictionary<string, string>
        {
            [$"M:{TypeId}.Args(Some.Other.Spelling)"] = "args",
            [$"M:{TypeId}.Overload(System.Int32)"] = "int",
            [$"M:{TypeId}.Overload(System.Double)"] = "double",
        };

        Assert.Equal("args", XmlDocSummaries.Lookup(docs, Method(nameof(Sample.Args))));
        Assert.Equal("int", XmlDocSummaries.Lookup(docs, typeof(Sample).GetMethod(nameof(Sample.Overload), [typeof(int)])!));
        Assert.Null(XmlDocSummaries.Lookup(docs, typeof(Sample).GetMethod(nameof(Sample.Overload), [typeof(string)])!));
    }
}
