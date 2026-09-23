using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Security;

namespace NutHub.Services.Notifications.Webhooks;

/// <summary>The outcome of one delivery.</summary>
internal sealed record DeliveryResult(bool Success, string? Error)
{
    public static readonly DeliveryResult Ok = new(true, null);

    public static DeliveryResult Fail(string error) => new(false, error);
}

/// <summary>
/// Calls a webhook: an HTTP request built from the event (a templated body or the default JSON document), with
/// the configured headers, a 10-second timeout per attempt and a few retries for failures that may be temporary.
/// TLS certificates are always validated: the headers often carry access tokens.
/// </summary>
internal sealed class WebhookSender(IHttpClientFactory httpClientFactory, ISecretProtector secrets, TimeProvider time,
                                    ILogger logger)
{
    public const string HttpClientName = "NutHub.Webhooks";

    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The pauses between attempts; tests shorten them.</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    public async Task<DeliveryResult> SendAsync(WebhookSettings hook, NotificationContext context, int maxAttempts,
                                                CancellationToken cancellationToken)
    {
        string? error = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            (bool retry, string? failure, TimeSpan? retryAfter) = await AttemptAsync(hook, context, cancellationToken)
                .ConfigureAwait(false);
            if (failure is null)
            {
                return DeliveryResult.Ok;
            }

            error = attempt > 1 ? $"{failure} (after {attempt} attempts)" : failure;
            if (!retry || attempt == maxAttempts)
            {
                break;
            }

            TimeSpan delay = retryAfter ?? RetryDelays[Math.Min(attempt - 1, RetryDelays.Count - 1)];
            logger.LogDebug("Webhook {Name}: {Error}; retrying in {Delay} s.", DisplayName(hook), failure, delay.TotalSeconds);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }

        return DeliveryResult.Fail(error ?? "Unknown error.");
    }

    /// <summary>What the web panel shows for a webhook: its name, or the host it calls (never the full URL, which may
    /// hold a token).</summary>
    public static string DisplayName(WebhookSettings hook)
    {
        if (!string.IsNullOrWhiteSpace(hook.Name))
        {
            return hook.Name;
        }

        return Uri.TryCreate(hook.Url, UriKind.Absolute, out Uri? uri) ? uri.Host : "webhook " + hook.Id;
    }

    /// <summary>Builds the request for one attempt (a request message cannot be sent twice).</summary>
    internal HttpRequestMessage BuildRequest(WebhookSettings hook, NotificationContext context)
    {
        IReadOnlyDictionary<string, string> values = context.PlaceholderValues();
        HttpMethod method = hook.Method.Trim().ToUpperInvariant() switch
        {
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "GET" => HttpMethod.Get,
            _ => throw new WebhookConfigurationException($"The method '{hook.Method}' is not supported; use POST, PUT or GET."),
        };

        string url = Placeholders.Apply(hook.Url.Trim(), values, PlaceholderEscaping.Url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebhookConfigurationException("The URL must be an absolute http:// or https:// address.");
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(NutHubInfo.ProductName, NutHubInfo.Version));

        if (method != HttpMethod.Get)
        {
            MediaTypeHeaderValue contentType = MediaTypeHeaderValue.TryParse(hook.ContentType, out MediaTypeHeaderValue? parsed)
                ? parsed
                : new MediaTypeHeaderValue("application/json");
            contentType.CharSet ??= "utf-8";
            string body = hook.BodyTemplate is { Length: > 0 } template
                ? Placeholders.Apply(template, values, Placeholders.ForContentType(contentType.MediaType))
                : BuildDefaultDocument(context);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = contentType;
        }

        foreach ((string name, string storedValue) in hook.Headers)
        {
            string value;
            try
            {
                value = secrets.Unprotect(storedValue) ?? "";
            }
            catch (Exception ex)
            {
                throw new WebhookConfigurationException(
                    $"The value of the header '{name}' cannot be decrypted (was the secret key replaced?). Enter it again.", ex);
            }

            // Placeholders work in header values too (ntfy titles...); a line break would split the header.
            value = Placeholders.Apply(value, values, PlaceholderEscaping.None).ReplaceLineEndings(" ");
            try
            {
                if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null)
                    {
                        request.Content.Headers.Remove(name);
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                }
                else
                {
                    request.Headers.Remove(name);
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                    {
                        throw new WebhookConfigurationException($"The header '{name}' is not valid.");
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                // Not a header name, or one that belongs to the body (Expires, Allow...): thrown, not returned.
                throw new WebhookConfigurationException($"The header '{name}' cannot be sent: {ex.Message}", ex);
            }
        }

        return request;
    }

    /// <summary>The JSON document sent when no body template is set.</summary>
    internal static string BuildDefaultDocument(NotificationContext context)
    {
        var e = context.Event;
        IReadOnlyDictionary<string, string> values = context.PlaceholderValues();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("server", context.ServerName);
            if (context.Location is not null)
            {
                w.WriteString("location", context.Location);
            }

            w.WriteString("ups", e.Ups);
            w.WriteString("type", context.TypeName);
            w.WriteString("notifyType", values["notifytype"]);
            w.WriteString("severity", values["severity"]);
            w.WriteString("category", values["category"]);
            w.WriteString("message", e.Message);
            w.WriteString("timestamp", values["timestamp"]);
            w.WriteString("actor", e.Actor);
            w.WriteStartObject("data");
            foreach ((string key, string value) in e.Data ?? new Dictionary<string, string>())
            {
                w.WriteString(key, value);
            }

            w.WriteEndObject();
            if (context.Readings is { } readings)
            {
                w.WriteStartObject("status");
                foreach ((string key, string value) in readings.AsVariables())
                {
                    w.WriteString(key, value);
                }

                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("status");
            }

            if (context.IsTest)
            {
                w.WriteBoolean("test", true);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<(bool Retry, string? Error, TimeSpan? RetryAfter)> AttemptAsync(
        WebhookSettings hook, NotificationContext context, CancellationToken cancellationToken)
    {
        HttpRequestMessage request;
        try
        {
            request = BuildRequest(hook, context);
        }
        catch (WebhookConfigurationException ex)
        {
            return (false, ex.Message, null);
        }

        using (request)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                HttpClient client = httpClientFactory.CreateClient(HttpClientName);
                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return (false, null, null);
                }

                string snippet = await ReadSnippetAsync(response, timeout.Token).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                string error = $"HTTP {code} {response.ReasonPhrase}".TrimEnd() + (snippet.Length > 0 ? ": " + snippet : "");
                bool retry = code is 408 or 429 || code >= 500;
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta is { } delta && delta <= TimeSpan.FromSeconds(30)
                    ? delta
                    : null;
                return (retry, error, retryAfter);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (true, $"No answer within {AttemptTimeout.TotalSeconds:0} s.", null);
            }
            catch (HttpRequestException ex)
            {
                string detail = ex.InnerException is { } inner && !ex.Message.Contains(inner.Message, StringComparison.Ordinal)
                    ? $"{ex.Message} ({inner.Message})"
                    : ex.Message;
                return (true, detail, null);
            }
        }
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[400];
            int total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, total).ReplaceLineEndings(" ").Trim();
            return text.Length > 200 ? text[..200] + "..." : text;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return "";
        }
    }
}

/// <summary>A webhook that cannot be called as configured: retrying would not help.</summary>
internal sealed class WebhookConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
