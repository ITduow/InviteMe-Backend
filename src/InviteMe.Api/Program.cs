using InviteMe.Api.Endpoints;
using InviteMe.Api.Extensions;
using InviteMe.Api.Hubs;
using InviteMe.Api.Middleware;
using InviteMe.Application;
using InviteMe.Infrastructure;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

// Load the nearest .env (including the repository root) before configuration is built.
// Environment variables supplied by the host take precedence over local values.
DotNetEnv.Env.NoClobber().TraversePath().Load();

// Load the nearest .env (including the repository root) before configuration is built.
// Environment variables supplied by the host take precedence over local values.
DotNetEnv.Env.NoClobber().TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddApi(builder.Configuration).AddRsvpApi().AddGiftApi().AddGuestAccessApi(builder.Configuration);

var app = builder.Build();

// Keep local databases up to date; production migrations run during deployment.
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();
app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "InviteMe API"));
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapApplicationHealth();
app.MapAccounts();
app.MapWeddingAccess();
app.MapWeddingSetup(builder.Configuration);
app.MapGet("/media/wedding-covers/{weddingId:guid}/{fileName}", async (Guid weddingId, string fileName,
    InviteMe.Application.Features.Weddings.Setup.WeddingSetupHandler handler, HttpContext context, CancellationToken ct) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[a-fA-F0-9]{48}\\.(jpg|png|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        return Results.NotFound();
    var stream = await handler.OpenCoverImageAsync(weddingId, fileName, ct);
    if (stream is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
    { ".jpg" => "image/jpeg", ".png" => "image/png", _ => "image/webp" };
    return Results.File(stream, contentType, enableRangeProcessing: false);
}).AllowAnonymous().WithTags("Wedding Media").WithName("GetWeddingCoverImage").Produces(200).Produces(404);
app.MapCoHosts();
app.MapAuditLogs();
app.MapRsvp();
app.MapGifts();
app.MapGuestPlanning();
app.MapInvitationWording();
app.MapInvitationMessaging();
app.MapInvitationDesign();
app.MapWeddingWebsite();
app.MapWeddingMedia();
app.MapGuestAccess();
app.MapThankYouTemplates();
app.MapThankYouMessages();
app.MapHub<WeddingHub>("/hubs/weddings", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization();
app.MapHub<PublicWeddingHub>("/hubs/public-weddings").RequireRateLimiting("guest-invitation");
app.Run();

public partial class Program;
