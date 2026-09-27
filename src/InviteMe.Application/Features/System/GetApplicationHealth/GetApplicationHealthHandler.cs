namespace InviteMe.Application.Features.System.GetApplicationHealth;

public sealed record ApplicationHealthResponse(string Application, string Status, DateTimeOffset Timestamp);

public sealed class GetApplicationHealthHandler(TimeProvider timeProvider)
{
    public Task<ApplicationHealthResponse> HandleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ApplicationHealthResponse("InviteMe", "ok", timeProvider.GetUtcNow()));
    }
}
