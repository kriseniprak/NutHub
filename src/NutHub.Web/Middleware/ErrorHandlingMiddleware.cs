using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NutHub.Web.Api;

namespace NutHub.Web.Middleware;

/// <summary>
/// Turns exceptions into the API error body: <see cref="ApiException"/> as given, malformed requests as
/// <c>badRequest</c>, anything else as <c>internal</c> with the details only in the server log.
/// </summary>
internal sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nobody is left to answer.
        }
        catch (ApiException ex)
        {
            await WriteAsync(context, ex.Status, ex.ToError()).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            // Unreadable JSON, wrong content type, body too large...
            int status = ex.StatusCode is StatusCodes.Status413PayloadTooLarge or StatusCodes.Status415UnsupportedMediaType
                ? ex.StatusCode
                : StatusCodes.Status400BadRequest;
            string message = ex.InnerException is JsonException json ? DescribeJsonError(json) : ex.Message;
            await WriteAsync(context, status, new ApiError(ErrorCodes.BadRequest, message)).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                             new ApiError(ErrorCodes.BadRequest, DescribeJsonError(ex))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The web request {Method} {Path} failed.", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                             new ApiError(ErrorCodes.Internal, "Unexpected error; see the server log.")).ConfigureAwait(false);
        }
    }

    private static string DescribeJsonError(JsonException ex) =>
        string.IsNullOrEmpty(ex.Path) || ex.Path == "$"
            ? "The request body is not valid JSON of the expected shape."
            : $"The request body has an invalid value at {ex.Path.TrimStart('$', '.')}.";

    private async Task WriteAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted)
        {
            logger.LogDebug("Could not report {Error} to the client: the answer had already started.", error.Error);
            return;
        }

        context.Response.Clear();
        await ApiErrorWriter.WriteAsync(context, status, error).ConfigureAwait(false);
    }
}
