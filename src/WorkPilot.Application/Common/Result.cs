namespace WorkPilot.Application.Common;

/// <summary>How a use case ended (spec 0018, section 4). Each status maps to one HTTP status at the Api.</summary>
public enum ResultStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
    Forbidden,

    /// <summary>The caller's <c>If-Match</c> version is stale (HTTP 412).</summary>
    PreconditionFailed,

    /// <summary>The write needs an <c>If-Match</c> version and none was sent (HTTP 428).</summary>
    PreconditionRequired,
}

/// <summary>
/// The outcome of a use case: a value on success, or an expected failure (spec 0018, section 4).
/// Expected failures are values, not exceptions; throwing is kept for bugs and broken invariants.
/// </summary>
/// <param name="Status">How the use case ended.</param>
/// <param name="Value">The result, set only when <paramref name="Status"/> is <see cref="ResultStatus.Ok"/>.</param>
/// <param name="Errors">Field errors, set for <see cref="ResultStatus.Invalid"/>.</param>
/// <param name="Detail">A human readable reason, shown to the user as is.</param>
public sealed record Result<T>(ResultStatus Status, T? Value, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null)
{
    public bool IsOk => Status == ResultStatus.Ok;

    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value);

    public static Result<T> NotFound(string? detail = null) => new(ResultStatus.NotFound, default, null, detail);

    public static Result<T> Invalid(string field, string message) =>
        new(ResultStatus.Invalid, default, new Dictionary<string, string[]> { [field] = [message] });

    public static Result<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(ResultStatus.Invalid, default, errors);

    public static Result<T> Conflict(string detail) => new(ResultStatus.Conflict, default, null, detail);

    public static Result<T> Forbidden(string? detail = null) => new(ResultStatus.Forbidden, default, null, detail);

    public static Result<T> PreconditionFailed(string detail) => new(ResultStatus.PreconditionFailed, default, null, detail);

    public static Result<T> PreconditionRequired(string detail) => new(ResultStatus.PreconditionRequired, default, null, detail);
}
