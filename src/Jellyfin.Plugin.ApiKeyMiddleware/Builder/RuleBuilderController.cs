using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Builder;

// KeyNames holds names only: tokens never leave the server through this endpoint.
public sealed record BuilderCatalog(IReadOnlyList<string> KeyNames, IReadOnlyList<CatalogEndpoint> Endpoints);

// Data for the rule builder on the configuration page.
[ApiController]
[Route("ApiKeyMiddleware")]
[Authorize(Policy = "RequiresElevation")] // Jellyfin.Api.Constants.Policies.RequiresElevation (admins only)
public class RuleBuilderController : ControllerBase
{
    private readonly EndpointDataSource _endpoints;
    private readonly XmlDocSummaries _summaries;
    private readonly IAuthenticationManager _authenticationManager;

    public RuleBuilderController(EndpointDataSource endpoints, XmlDocSummaries summaries, IAuthenticationManager authenticationManager)
    {
        _endpoints = endpoints;
        _summaries = summaries;
        _authenticationManager = authenticationManager;
    }

    [HttpGet("Catalog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<BuilderCatalog>> GetCatalog()
    {
        var keys = await _authenticationManager.GetApiKeys().ConfigureAwait(false);
        var names = keys
            .Select(k => k.AppName?.Trim())
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BuilderCatalog(names, EndpointCatalog.Build(_endpoints.Endpoints, _summaries.Get));
    }
}
