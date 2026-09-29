using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WorkPilot.Web.Features.Common;

/// <summary>The outcome of an Api write made from the Web: a value, or a message to show the user (spec 0018, section 4).</summary>
public sealed record ApiResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null && Value is not null;

    public static ApiResult<T> Ok(T value) => new(value, null);

    public static ApiResult<T> Fail(string error) => new(default, error);
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

        var message = await ProblemMessageAsync(response, cancellationToken);
        return ApiResult<T>.Fail(message ?? (response.StatusCode == HttpStatusCode.NotFound
            ? notFoundMessage
            : $"The request failed ({(int)response.StatusCode})."));
    }

    // The validation errors (joined), else the detail, else null.
    private static async Task<string?> ProblemMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(p => p.Value.EnumerateArray().Select(m => m.GetString()))
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
                if (messages.Count > 0)
                {
                    return string.Join(" ", messages);
                }
            }

            return root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(detail.GetString())
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
