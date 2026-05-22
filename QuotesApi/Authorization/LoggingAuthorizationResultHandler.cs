using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace QuotesApi.Authorization;

// Intercepts every authorization decision before the scheme writes the response.
// Runs after UseAuthentication and after policy evaluation — so TraceId is already
// in the Serilog LogContext and both logs below are automatically correlated.
//
//   Challenged → user was not authenticated (HTTP 401 path)
//   Forbidden  → user was authenticated but lacked the required permission (HTTP 403 path)
public sealed class LoggingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();
    private readonly ILogger _logger;

    public LoggingAuthorizationResultHandler(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger("QuotesApi.Security");
    }

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            _logger.LogWarning(
                "Authorization challenge — request is unauthenticated Method={Method} Path={Path}",
                context.Request.Method,
                context.Request.Path.Value);
        }
        else if (authorizeResult.Forbidden)
        {
            _logger.LogWarning(
                "Authorization forbidden — authenticated but not authorized Method={Method} Path={Path}",
                context.Request.Method,
                context.Request.Path.Value);
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
