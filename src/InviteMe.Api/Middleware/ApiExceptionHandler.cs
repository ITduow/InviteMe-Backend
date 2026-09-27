using FluentValidation;
using InviteMe.Application.Common.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Api.Middleware;

internal sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            ValidationException => (400, "VALIDATION_FAILED", "One or more fields are invalid."),
            BadHttpRequestException bad => (bad.StatusCode, "INVALID_REQUEST", "The request could not be read."),
            ApplicationProblemException problem => (StatusFor(problem.Kind), problem.Code, problem.Message),
            DbUpdateConcurrencyException => (409, "CONCURRENCY_CONFLICT", "Data changed. Refresh and retry."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                => (409, "DUPLICATE_RECORD", "The record already exists."),
            _ => (500, "INTERNAL_ERROR", "An unexpected error occurred.")
        };
        if (status == 500)
            logger.LogError("Request failed. ExceptionType={ExceptionType} CorrelationId={CorrelationId}",
                exception.GetType().Name, context.TraceIdentifier);

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}"
        };
        problemDetails.Extensions["code"] = code;
        if (exception is ValidationException validation)
            problemDetails.Extensions["errors"] = validation.Errors.GroupBy(x => x.PropertyName)
                .ToDictionary(x => x.Key, x => x.Select(f => f.ErrorMessage).Distinct().ToArray());

        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problemDetails });
        return true;
    }

    private static int StatusFor(ProblemKind kind) => kind switch
    {
        ProblemKind.Unauthenticated => 401,
        ProblemKind.Forbidden => 403,
        ProblemKind.NotFound => 404,
        ProblemKind.Conflict => 409,
        ProblemKind.BusinessRule => 422,
        _ => 500
    };
}
