using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

public sealed record ScopeStatus(IReadOnlyList<string> Errors, IReadOnlyList<ObservedRequest> Observed);

// For the configuration page: rule errors and the requests seen since the last restart.
[ApiController]
[Route("ApiKeyMiddleware")]
[Authorize(Policy = "RequiresElevation")] // Jellyfin.Api.Constants.Policies.RequiresElevation (admins only)
public class ScopeStatusController : ControllerBase
{
    private readonly IScopeSettings _settings;
    private readonly RulesCache _rules;
    private readonly ObservedRequests _observed;

    public ScopeStatusController(IScopeSettings settings, RulesCache rules, ObservedRequests observed)
    {
        _settings = settings;
        _rules = rules;
        _observed = observed;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScopeStatus> GetStatus()
        => new ScopeStatus(_rules.Get(_settings.Rules).Errors, _observed.Snapshot());
}
