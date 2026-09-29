using System.Net;
using System.Text;
using WorkPilot.Web.Features.Common;

namespace WorkPilot.Web.Tests;

// The one Web reader for Api responses (spec 0018, section 4): every Api
// failure is ProblemDetails, turned into a message a page can show.
public class ApiResultReaderTests
{
    private sealed record Thing(string Name);

    [Fact]
    public async Task ASuccess_CarriesTheValue()
    {
        var result = await ReadAsync(HttpStatusCode.OK, """{"name":"Main CV"}""");

        Assert.True(result.Succeeded);
        Assert.Equal("Main CV", result.Value!.Name);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ASuccessWithAnEmptyJsonBody_IsNotASuccess()
    {
        var result = await ReadAsync(HttpStatusCode.OK, "null");

        Assert.False(result.Succeeded);
        Assert.Equal("The response was empty.", result.Error);
    }

    [Fact]
    public async Task AValidationProblem_JoinsEveryFieldMessage()
    {
        var result = await ReadAsync(HttpStatusCode.BadRequest, """{"status":400,"errors":{"name":["Name is required."],"content":["Content is too long."]}}""");

        Assert.False(result.Succeeded);
        Assert.Equal("Name is required. Content is too long.", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AProblemWithADetail_ShowsTheDetailAsIs(HttpStatusCode status)
    {
        var result = await ReadAsync(status, """{"status":409,"detail":"The resume changed while you were editing."}""");

        Assert.Equal("The resume changed while you were editing.", result.Error);
    }

    [Fact]
    public async Task ANotFoundWithoutADetail_ShowsTheCallersMessage()
    {
        var result = await ReadAsync(HttpStatusCode.NotFound, """{"status":404,"title":"Not Found"}""");

        Assert.Equal("That thing no longer exists.", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"errors":{"name":"not an array"}}""")]
    public async Task AnUnreadableOrEmptyErrorBody_FallsBackToTheStatusCode(string body)
    {
        var result = await ReadAsync(HttpStatusCode.InternalServerError, body);

        Assert.Equal("The request failed (500).", result.Error);
    }

    private static Task<ApiResult<Thing>> ReadAsync(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return ApiResultReader.ReadAsync<Thing>(response, "That thing no longer exists.");
    }
}
