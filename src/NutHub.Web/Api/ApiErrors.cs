using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace NutHub.Web.Api;

/// <summary>The error codes of docs/API.md; the panel localises its messages by these.</summary>
internal static class ErrorCodes
{
    public const string Validation = "validation";
    public const string BadRequest = "badRequest";
    public const string Csrf = "csrf";
    public const string Unauthorized = "unauthorized";
    public const string InvalidCredentials = "invalidCredentials";
    public const string Forbidden = "forbidden";
    public const string PasswordChangeRequired = "passwordChangeRequired";
    public const string NotFound = "notFound";
    public const string Conflict = "conflict";
    public const string LastAdmin = "lastAdmin";
    public const string Self = "self";
    public const string RateLimited = "rateLimited";
    public const string Internal = "internal";
}

/// <summary>The error body of every failed API call.</summary>
internal sealed record ApiError(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds = null);

/// <summary>
/// Thrown anywhere below an endpoint (including inside a configuration mutation, which then saves nothing) to end
/// the request with an API error; the error middleware turns it into the JSON answer.
/// </summary>
internal sealed class ApiException(int status, string code, string message,
                                   IReadOnlyDictionary<string, string>? fields = null) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string>? Fields { get; } = fields;

    public ApiError ToError() => new(Code, Message, Fields);

    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    public static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    public static ApiException BadRequest(string message) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, message);

    public static ApiException Validation(IReadOnlyDictionary<string, string> fields, string? message = null) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.Validation,
            message ?? "Some values are not valid: " + string.Join(" ", fields.Values), fields);

    public static ApiException Validation(string field, string message) =>
        Validation(new Dictionary<string, string> { [field] = message }, message);
}

/// <summary>Writes API errors outside of endpoints (middleware, authentication events).</summary>
internal static class ApiErrorWriter
{
    public static Task WriteAsync(HttpContext context, int status, string code, string message,
                                  IReadOnlyDictionary<string, string>? fields = null, int? retryAfterSeconds = null) =>
        WriteAsync(context, status, new ApiError(code, message, fields, retryAfterSeconds));

    public static async Task WriteAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(error, ApiJson.Options, context.RequestAborted).ConfigureAwait(false);
    }

    public static IResult Result(int status, string code, string message,
                                 IReadOnlyDictionary<string, string>? fields = null) =>
        Results.Json(new ApiError(code, message, fields), ApiJson.Options, statusCode: status);
}
