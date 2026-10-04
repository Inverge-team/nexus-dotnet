using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus;

/// <summary>
/// Base class for every failure the SDK raises, so one <c>catch</c> contains it.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     await nexus.Push.SendToUserAsync("user_1", title: "Hi");
/// }
/// catch (NexusException ex)
/// {
///     logger.LogWarning(ex, "Nexus unavailable");
/// }
/// </code>
/// </example>
public class NexusException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NexusException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public NexusException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>The SDK was configured wrongly: no API key, a malformed origin, and so on.</summary>
public sealed class NexusConfigurationException : NexusException
{
    /// <summary>Creates the exception.</summary>
    public NexusConfigurationException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public NexusConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A network-level failure: DNS, refused connection, TLS, or a timeout. The
/// original exception is in <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class NexusTransportException : NexusException
{
    /// <summary>Creates the exception.</summary>
    public NexusTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A non-success response from the API.
/// </summary>
/// <remarks>
/// Nexus answers failures with <c>{"error":{"code","message","details"}}</c>.
/// Those three land in <see cref="Code"/>, <see cref="Exception.Message"/> and
/// <see cref="Details"/>. Prefer matching on <see cref="Code"/> over the message:
/// codes are part of the contract, messages are not.
/// </remarks>
public class NexusApiException : NexusException
{
    /// <summary>Creates the exception.</summary>
    public NexusApiException(
        string message,
        int status,
        string? code = null,
        JsonNode? details = null,
        string? method = null,
        string? path = null,
        string? responseBody = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
        Method = method;
        Path = path;
        ResponseBody = responseBody;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The machine-readable error code, e.g. <c>validation_failed</c>.</summary>
    public string? Code { get; }

    /// <summary>Structured detail the server attached, when any.</summary>
    public JsonNode? Details { get; }

    /// <summary>The HTTP method of the failed request.</summary>
    public string? Method { get; }

    /// <summary>The path of the failed request.</summary>
    public string? Path { get; }

    /// <summary>The raw response body, truncated.</summary>
    public string? ResponseBody { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var where = Path is null ? string.Empty : " on " + Method + " " + Path;
        var code = string.IsNullOrEmpty(Code) ? string.Empty : " [" + Code + "]";
        return string.Format(
            CultureInfo.InvariantCulture,
            "Nexus API error {0}{1}{2}: {3}",
            Status,
            code,
            where,
            Message);
    }
}

/// <summary>401 — the API key is missing, malformed, or revoked.</summary>
public sealed class NexusAuthenticationException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusAuthenticationException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>403 — the key is valid but not allowed to do this.</summary>
public sealed class NexusPermissionDeniedException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusPermissionDeniedException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>404 — no such resource in this environment.</summary>
public sealed class NexusNotFoundException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusNotFoundException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>400 or 422 — the request body failed server-side validation.</summary>
public sealed class NexusValidationException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusValidationException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>
/// 402 — the organisation's data plane is suspended for unpaid invoices.
/// </summary>
/// <remarks>
/// Every <c>/partner/*</c> endpoint answers this while an organisation has
/// invoices past due beyond the grace period; the control plane and payment
/// callbacks stay open. Retrying cannot help, so the SDK never does: catch this
/// distinctly from an authentication failure and surface it to whoever can pay
/// the bill.
/// </remarks>
public sealed class NexusBillingSuspendedException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusBillingSuspendedException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>429 — too many requests.</summary>
public sealed class NexusRateLimitException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusRateLimitException(
        string message,
        int status,
        string? code,
        JsonNode? details,
        string? method,
        string? path,
        string? responseBody,
        TimeSpan? retryAfter)
        : base(message, status, code, details, method, path, responseBody)
        => RetryAfter = retryAfter;

    /// <summary>How long the server asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>5xx — the API failed. Idempotent calls are safe to repeat.</summary>
public sealed class NexusServerException : NexusApiException
{
    /// <summary>Creates the exception.</summary>
    public NexusServerException(
        string message, int status, string? code, JsonNode? details, string? method, string? path, string? responseBody)
        : base(message, status, code, details, method, path, responseBody)
    {
    }
}

/// <summary>Maps a failed response onto the most specific exception type.</summary>
internal static class NexusApiExceptionFactory
{
    public static NexusApiException Create(
        int status,
        JsonNode? payload,
        string? method,
        string? path,
        string? rawBody,
        IReadOnlyDictionary<string, string>? headers)
    {
        // The envelope is {"error": {...}}; tolerate a flat body from a proxy or
        // a gateway that never reached the application.
        var error = payload.Obj("error") ?? payload;

        var message = error.Str("message") ?? error.Str("error");
        if (string.IsNullOrEmpty(message))
        {
            message = string.Format(
                CultureInfo.InvariantCulture,
                "Nexus API request failed with status {0}.",
                status);
        }

        var code = error.Str("code");
        var details = error.Prop("details")?.DeepClone();
        var body = Truncate(rawBody);

        if (status == 402 || string.Equals(code, "billing_suspended", StringComparison.Ordinal))
        {
            return new NexusBillingSuspendedException(message!, status, code, details, method, path, body);
        }

        switch (status)
        {
            case 400:
            case 422:
                return new NexusValidationException(message!, status, code, details, method, path, body);
            case 401:
                return new NexusAuthenticationException(message!, status, code, details, method, path, body);
            case 403:
                return new NexusPermissionDeniedException(message!, status, code, details, method, path, body);
            case 404:
                return new NexusNotFoundException(message!, status, code, details, method, path, body);
            case 429:
                return new NexusRateLimitException(
                    message!, status, code, details, method, path, body, ReadRetryAfter(headers));
            default:
                return status >= 500
                    ? new NexusServerException(message!, status, code, details, method, path, body)
                    : new NexusApiException(message!, status, code, details, method, path, body);
        }
    }

    /// <summary>
    /// Reads <c>Retry-After</c>, which is either a delay in seconds or an HTTP date.
    /// </summary>
    public static TimeSpan? ReadRetryAfter(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.TryGetValue("retry-after", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            var delay = when - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static string? Truncate(string? body)
        => body is null || body.Length <= 2000 ? body : body.Substring(0, 2000);
}
