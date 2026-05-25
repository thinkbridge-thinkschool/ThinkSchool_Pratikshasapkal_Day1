using Microsoft.AspNetCore.Authorization;
using QuotesApi.Models;
using QuotesApi.Telemetry;
using System.Diagnostics;
using System.Security.Claims;

namespace QuotesApi.Authorization;

public class DeleteOwnQuoteHandler
    : AuthorizationHandler<DeleteOwnQuoteRequirement, Quote>
{
    private readonly ILogger<DeleteOwnQuoteHandler> _logger;

    public DeleteOwnQuoteHandler(ILogger<DeleteOwnQuoteHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DeleteOwnQuoteRequirement requirement,
        Quote resource)
    {
        using var activity = AppActivitySource.Instance.StartActivity("authz.quote_delete");
        activity?.SetTag("quote.id", resource.Id);

        var userEmail = context.User.FindFirstValue(ClaimTypes.Email);

        if (userEmail is not null && userEmail == resource.CreatedByEmail)
        {
            activity?.SetTag("authz.result", "allowed");
            context.Succeed(requirement);
        }
        else
        {
            activity?.SetTag("authz.result", "denied");
            _logger.LogWarning(
                "Quote delete denied QuoteId={QuoteId} OwnedBy={OwnerEmail} AttemptedBy={UserEmail}",
                resource.Id,
                resource.CreatedByEmail,
                userEmail ?? "anonymous");
        }

        return Task.CompletedTask;
    }
}
