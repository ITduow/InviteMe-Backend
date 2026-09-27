using System.Diagnostics;

namespace InviteMe.Api.Middleware;

internal sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Use a server-generated value: do not trust caller-supplied log content.
        var correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        context.TraceIdentifier = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}
