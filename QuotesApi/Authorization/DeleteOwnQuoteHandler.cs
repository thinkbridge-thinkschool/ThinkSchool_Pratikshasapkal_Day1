using Microsoft.AspNetCore.Authorization;
using QuotesApi.Models;
using System.Security.Claims;

namespace QuotesApi.Authorization;

public class DeleteOwnQuoteHandler
    : AuthorizationHandler<DeleteOwnQuoteRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DeleteOwnQuoteRequirement requirement,
        Quote resource)
    {
        var userEmail = context.User.FindFirstValue(ClaimTypes.Email);

        if (userEmail is not null && userEmail == resource.CreatedByEmail)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
