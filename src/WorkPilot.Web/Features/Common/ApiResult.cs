using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WorkPilot.Web.Features.Common;

/// <summary>The outcome of an Api write made from the Web: a value, or a message to show the user (spec 0018, section 4).</summary>
/// <param name="FieldErrors">A validation failure's messages per field, as the Api named them, for showing next to each field (spec 0021, AC-6).</param>
/// <param name="Status">The Api's status code for a failure, when there was a response.</param>
public sealed record ApiResult<T>(T? Value, string? Error, IReadOnlyDictionary<string, string[]>? FieldErrors = null, HttpStatusCode? Status = null)
{
    public bool Succeeded => Error is null && Value is not null;

    /// <summary>The Api answered 404.</summary>
    public bool IsNotFound => Status == HttpStatusCode.NotFound;

    public static ApiResult<T> Ok(T value) => new(value, null);

    public static ApiResult<T> Fail(string error, IReadOnlyDictionary<string, string[]>? fieldErrors = null, HttpStatusCode? status = null) =>
        new(default, error, fieldErrors, status);
}

/// <summary>
/// Reads any Api response into an <see cref="ApiResult{T}"/>. Every Api failure is RFC 7807
/// ProblemDetails (spec 0018, section 4): validation errors are joined into one message, otherwise
/// the <c>detail</c> is shown as is.
/// </summary>
public static class ApiResultReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads <paramref name="response"/>. A 404 without a <c>detail</c> shows <paramref name="notFoundMessage"/>;
    /// any other failure without one shows a generic message with the status code.
    /// </summary>
    public static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, string notFoundMessage, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            return value is null ? ApiResult<T>.Fail("The response was empty.") : ApiResult<T>.Ok(value);
        }

        var (message, fieldErrors) = await ProblemMessageAsync(response, cancellationToken);
        return ApiResult<T>.Fail(
            message ?? (response.StatusCode == HttpStatusCode.NotFound
                ? notFoundMessage
                : $"The request failed ({(int)response.StatusCode})."),
            fieldErrors,
            response.StatusCode);
    }

    // The validation errors (joined, and per field), else the detail, else null.
    private static async Task<(string? Message, IReadOnlyDictionary<string, string[]>? FieldErrors)> ProblemMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var fields = errors.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .Select(p => (p.Name, Messages: p.Value.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        .Select(m => m.GetString()!)
                        .ToArray()))
                    .Where(f => f.Messages.Length > 0)
                    .ToList();
                if (fields.Count > 0)
                {
                    return (string.Join(" ", fields.SelectMany(f => f.Messages)), fields.ToDictionary(f => f.Name, f => f.Messages));
                }
            }

            var message = root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(detail.GetString())
                ? detail.GetString()
                : null;
            return (message, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
