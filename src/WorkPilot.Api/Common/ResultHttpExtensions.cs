using WorkPilot.Application.Common;

namespace WorkPilot.Api.Common;

/// <summary>
/// The one mapper from a use case's <see cref="Result{T}"/> to HTTP (spec 0018, section 4). Every
/// error body is RFC 7807 ProblemDetails, so the Web reads every failure the same way.
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>
    /// <c>Ok</c> → <paramref name="ok"/>; <c>NotFound</c> → 404; <c>Invalid</c> → 400 validation
    /// problem; <c>Conflict</c> → 409; <c>Forbidden</c> → 403.
    /// </summary>
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> ok) => result.Status switch
    {
        ResultStatus.Ok => ok(result.Value!),
        ResultStatus.NotFound => Results.Problem(detail: result.Detail, statusCode: StatusCodes.Status404NotFound),
        ResultStatus.Invalid => Results.ValidationProblem(result.Errors?.ToDictionary() ?? [], detail: result.Detail),
        ResultStatus.Conflict => Results.Problem(detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
        ResultStatus.Forbidden => Results.Problem(detail: result.Detail, statusCode: StatusCodes.Status403Forbidden),
        _ => throw new InvalidOperationException($"Unhandled result status {result.Status}."),
    };
}
