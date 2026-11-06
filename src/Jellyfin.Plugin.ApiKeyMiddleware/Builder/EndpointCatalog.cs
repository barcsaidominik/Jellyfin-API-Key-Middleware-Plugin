using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using System.Reflection;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Builder;

// Who can call an endpoint without an API key. Every API key is a full admin, so this is only a
// hint for the rule builder: Admin endpoints are the ones a leaked key should rarely reach.
public enum EndpointAccess
{
    Anonymous,
    User,
    Admin
}

// One line of the rule builder: a rule ("GET /Users/*/Items/Latest") and what it covers.
public sealed record CatalogEndpoint(
    string Group,
    string Method,
    string Rule,
    IReadOnlyList<string> Templates,
    string? Summary,
    EndpointAccess Access,
    IReadOnlyList<string> Policies,
    bool Hidden,
    bool Obsolete);

// Every route the server serves (from routing, so including the ones hidden from the OpenAPI
// document, e.g. legacy /Users/{userId}/... routes), translated to the rule syntax.
public static class EndpointCatalog
{
    private const string OtherGroup = "Other";

    public static IReadOnlyList<CatalogEndpoint> Build(IEnumerable<Endpoint> endpoints, Func<MethodInfo, string?> summaries)
    {
        var rows = new List<CatalogEndpoint>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                continue;
            }

            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy ?? "Default")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var anonymous = policies.Length == 0 || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var access = anonymous ? EndpointAccess.Anonymous
                : policies.Any(p => p.Contains("Elevat", StringComparison.OrdinalIgnoreCase)) ? EndpointAccess.Admin
                : EndpointAccess.User;
            var template = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var summary = action is null ? null : summaries(action.MethodInfo);
            var hidden = endpoint.Metadata.GetMetadata<ApiExplorerSettingsAttribute>()?.IgnoreApi == true;
            var obsolete = endpoint.Metadata.GetMetadata<ObsoleteAttribute>() is not null;

            foreach (var rule in RulePaths(endpoint.RoutePattern))
            {
                foreach (var method in methods)
                {
                    rows.Add(new CatalogEndpoint(
                        action?.ControllerName ?? OtherGroup,
                        method.ToUpperInvariant(),
                        rule,
                        [template],
                        summary,
                        access,
                        policies,
                        hidden,
                        obsolete));
                }
            }
        }

        var merged = rows
            .GroupBy(r => (r.Method, Rule: r.Rule.ToUpperInvariant()))
            .Select(Merge)
            .ToList();

        // A GET rule also covers HEAD, so a HEAD line next to the same GET line adds nothing.
        var gets = merged.Where(r => r.Method == "GET").Select(r => r.Rule).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return merged
            .Where(r => r.Method != "HEAD" || !gets.Contains(r.Rule))
            .OrderBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Rule, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToList();
    }

    // Parameters become "*" (one segment) and a catch-all becomes "**". A segment that mixes text
    // and parameters ("{segmentId}.{container}") can only be "*". A trailing optional parameter
    // gives two rules, with and without that segment.
    internal static IReadOnlyList<string> RulePaths(RoutePattern pattern)
    {
        var segments = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            var parameters = segment.Parts.OfType<RoutePatternParameterPart>().ToList();
            if (parameters.Any(p => p.IsCatchAll))
            {
                segments.Add("**");
                return [Join(segments)];
            }

            if (parameters.Count == 0)
            {
                segments.Add(string.Concat(segment.Parts.Select(p => p switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternSeparatorPart separator => separator.Content,
                    _ => string.Empty
                })));
                continue;
            }

            segments.Add("*");
        }

        var last = pattern.PathSegments.Count > 0 ? pattern.PathSegments[^1] : null;
        var optionalTail = last is { Parts: [RoutePatternParameterPart { IsOptional: true }] };
        return optionalTail ? [Join(segments[..^1]), Join(segments)] : [Join(segments)];
    }

    private static string Join(IEnumerable<string> segments) => "/" + string.Join('/', segments);

    // Several routes can end up as the same rule. The merged line shows the strictest access level,
    // so a warning is never lost, and counts as hidden/obsolete only if all of them are.
    private static CatalogEndpoint Merge(IEnumerable<CatalogEndpoint> group)
    {
        var items = group.ToList();
        var first = items[0];
        return first with
        {
            Templates = items.SelectMany(i => i.Templates).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Summary = items.Select(i => i.Summary).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
            Access = items.Max(i => i.Access),
            Policies = items.SelectMany(i => i.Policies).Distinct(StringComparer.Ordinal).ToArray(),
            Hidden = items.All(i => i.Hidden),
            Obsolete = items.All(i => i.Obsolete),
        };
    }
}
