using InviteMe.Application.Common.Errors;

namespace InviteMe.Api.Authentication;

// Browser callers supply Origin automatically. Non-browser clients must supply it explicitly.
internal sealed class AuthOriginFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var origin = context.HttpContext.Request.Headers.Origin;
        var allowed = new Uri(configuration["Frontend:Url"]!).GetLeftPart(UriPartial.Authority);
        if (origin.Count != 1 || !string.Equals(origin[0], allowed, StringComparison.Ordinal))
        {
            throw new ApplicationProblemException(ProblemKind.Forbidden, "CSRF_ORIGIN_DENIED",
                "A trusted frontend Origin header is required.");
        }

        return await next(context);
    }
}
