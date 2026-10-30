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

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration, builder.Environment).AddApi(builder.Configuration).AddRsvpApi().AddGiftApi().AddGuestAccessApi(builder.Configuration);
builder.Services.AddCors(options =>
{
    var frontend = builder.Configuration["Frontend:Url"];
    if (!Uri.TryCreate(frontend, UriKind.Absolute, out var frontendUri) ||
        (frontendUri.Scheme != "https" && frontendUri.Scheme != "http") ||
        frontendUri.AbsolutePath != "/" || !string.IsNullOrEmpty(frontendUri.Query) ||
        !string.IsNullOrEmpty(frontendUri.Fragment) || !string.IsNullOrEmpty(frontendUri.UserInfo))
        throw new InvalidOperationException("Frontend:Url must be an HTTP(S) origin without a path.");

    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(frontendUri.GetLeftPart(UriPartial.Authority))
            .AllowAnyHeader().AllowAnyMethod().AllowCredentials()
            // The app and the API are different origins, so every authenticated call needs a preflight; let the
            // browser reuse it (Chromium caps this at 2 hours) instead of sending OPTIONS before each request.
            .SetPreflightMaxAge(TimeSpan.FromHours(2)));
});

var app = builder.Build();

// The Elastic Beanstalk deployment does not run migrations separately.
// Apply pending migrations before hosted workers and HTTP requests start.
if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
var httpsTerminatesAtProxy = builder.Configuration.GetValue<bool>("Https:TerminateAtProxy");
if (!app.Environment.IsDevelopment() || httpsTerminatesAtProxy)
    app.UseHsts();
if (!httpsTerminatesAtProxy)
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
app.MapAdminManagement();
app.MapGet("/media/template-assets/{templateId:guid}/{fileName}", async (Guid templateId, string fileName,
    InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage media, HttpContext context, CancellationToken ct) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[a-f0-9]{48}\\.(jpg|png|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        return Results.NotFound();
    var stream = await media.OpenReadAsync(templateId, fileName, ct);
    if (stream is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var contentType = Path.GetExtension(fileName) switch
    { ".jpg" => "image/jpeg", ".png" => "image/png", _ => "image/webp" };
    return Results.File(stream, contentType, enableRangeProcessing: false);
}).AllowAnonymous().WithTags("Templates").WithName("GetTemplateAsset").Produces(200).Produces(404);
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
app.MapNotifications();
app.MapGifts();
app.MapGuestPlanning();
app.MapInvitationWording();
app.MapInvitationMessaging();
app.MapTextbeeWebhook();
app.MapInvitationDesign();
app.MapWeddingWebsite();
app.MapPlans();
app.MapWeddingMedia();
app.MapGuestAccess();
app.MapThankYouTemplates();
app.MapThankYouMessages();
app.MapHub<WeddingHub>("/hubs/weddings", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization();
app.MapHub<PublicWeddingHub>("/hubs/public-weddings").RequireRateLimiting("guest-invitation");
app.Run();

public partial class Program;
