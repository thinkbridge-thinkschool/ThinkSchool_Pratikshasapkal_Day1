using Microsoft.AspNetCore.Authorization;
using QuotesApi.Models;
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
        var userEmail = context.User.FindFirstValue(ClaimTypes.Email);

        if (userEmail is not null && userEmail == resource.CreatedByEmail)
        {
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogWarning(
                "Quote delete denied QuoteId={QuoteId} OwnedBy={OwnerEmail} AttemptedBy={UserEmail}",
                resource.Id,
                resource.CreatedByEmail,
                userEmail ?? "anonymous");
        }

        return Task.CompletedTask;
    }
}
