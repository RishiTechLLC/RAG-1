using System.ClientModel;
using Azure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Turns failures from Azure services (AI Search, Storage, Azure OpenAI) into a readable problem response
/// such as "Azure OpenAI returned 404 DeploymentNotFound: ...", instead of an opaque 500. In Development
/// every exception's message is included; in other environments only the Azure service errors are.
/// </summary>
internal sealed class UpstreamExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<UpstreamExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            RequestFailedException azure => (StatusCodes.Status502BadGateway,
                $"An Azure service returned {azure.Status} {azure.ErrorCode}",
                FirstLine(azure.Message)),
            ClientResultException openAi => (StatusCodes.Status502BadGateway,
                $"Azure OpenAI returned {openAi.Status}",
                FirstLine(openAi.Message)),
            _ when environment.IsDevelopment() => (StatusCodes.Status500InternalServerError,
                exception.GetType().Name,
                exception.Message),
            _ => (0, null, null),
        };

        if (status == 0)
        {
            return false; // fall back to the default handler (generic 500, no details)
        }

        logger.LogError(exception, "{Title}: {Detail}", title, detail);
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }

    private static string FirstLine(string message)
    {
        // Azure SDK messages append "Status: ...", the raw response and headers after the summary.
        var end = message.IndexOf("\nStatus:", StringComparison.Ordinal);
        return (end > 0 ? message[..end] : message).Trim();
    }
}
