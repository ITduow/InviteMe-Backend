using InviteMe.Api.Endpoints;
using InviteMe.Api.Extensions;
using InviteMe.Api.Hubs;
using InviteMe.Api.Middleware;
using InviteMe.Application;
using InviteMe.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddApi(builder.Configuration);

var app = builder.Build();
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
app.MapWorkspaces();
app.MapHub<WeddingHub>("/hubs/weddings", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization();
app.Run();

public partial class Program;
