using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MeetingActions.Infrastructure.Llm;
using MongoDB.Driver;

namespace MeetingActions.Api;

/// <summary>Returns a safe RFC 7807 response for unhandled API exceptions.</summary>
public sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Logs the failure and writes a generic problem response without stack details.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled request exception for trace {TraceId}", httpContext.TraceIdentifier);
        var databaseUnavailable = exception is MongoException or TimeoutException;
        var llmUnavailable = exception is LlmUnavailableException;
        httpContext.Response.StatusCode = databaseUnavailable || llmUnavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = databaseUnavailable
                    ? "Database unavailable."
                    : llmUnavailable ? "Transcript analysis provider unavailable." : "An unexpected error occurred.",
                Detail = databaseUnavailable
                    ? "The database is temporarily unavailable."
                    : exception is LlmUnavailableException llmException
                        ? llmException.Message
                    : null,
                Instance = httpContext.Request.Path
            }
        });
        return true;
    }
}
