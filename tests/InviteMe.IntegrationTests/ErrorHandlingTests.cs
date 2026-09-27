using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using InviteMe.Application.Common.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("validation", 400, "VALIDATION_FAILED")]
    [InlineData("forbidden", 403, "WEDDING_ACCESS_DENIED")]
    [InlineData("concurrency", 409, "CONCURRENCY_CONFLICT")]
    [InlineData("unexpected", 500, "INTERNAL_ERROR")]
    public async Task HandlerProducesSafeProblemDetails(string scenario, int status, string code)
    {
        using var client = factory.CreateHttpsClient();
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = "test-correlation" };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        Exception exception = scenario switch
        {
            "validation" => new ValidationException([new ValidationFailure("Name", "Name is required.")]),
            "forbidden" => new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Access denied."),
            "concurrency" => new DbUpdateConcurrencyException("private database details"),
            _ => new InvalidOperationException("private database details")
        };
        var handler = scope.ServiceProvider.GetServices<IExceptionHandler>().Single();
        Assert.True(await handler.TryHandleAsync(context, exception, default));
        Assert.Equal(status, context.Response.StatusCode);
        body.Position = 0;
        var payload = await new StreamReader(body).ReadToEndAsync();
        Assert.DoesNotContain("private database details", payload);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-correlation", json.RootElement.GetProperty("traceId").GetString());
        if (scenario == "validation")
            Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("Name", out _));
    }

}
