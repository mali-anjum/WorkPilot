using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Api.Common;
using WorkPilot.Application.Common;

namespace WorkPilot.Api.Tests;

// One error shape for every endpoint (spec 0018, section 4): Result<T> maps
// to one status each, and every failure body is RFC 7807 ProblemDetails.
[Collection("Api")]
public class ErrorShapeTests(SharedApiFactory factory)
{
    [Fact]
    public async Task Ok_UsesTheSuccessResultTheEndpointChose()
    {
        var (status, _, _) = await ExecuteAsync(Result<string>.Ok("made"), value => Results.Created($"/things/{value}", value));

        Assert.Equal(StatusCodes.Status201Created, status);
    }

    [Theory]
    [InlineData(ResultStatus.NotFound, 404)]
    [InlineData(ResultStatus.Conflict, 409)]
    [InlineData(ResultStatus.Forbidden, 403)]
    public async Task Failures_BecomeProblemDetailsWithTheirStatusAndDetail(ResultStatus kind, int expectedStatus)
    {
        var result = new Result<string>(kind, null, null, "Something specific went wrong.");

        var (status, contentType, body) = await ExecuteAsync(result, Results.Ok);

        Assert.Equal(expectedStatus, status);
        Assert.StartsWith("application/problem+json", contentType);
        Assert.Equal(expectedStatus, body.GetProperty("status").GetInt32());
        Assert.Equal("Something specific went wrong.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Invalid_BecomesAValidationProblemCarryingTheFieldErrors()
    {
        var (status, contentType, body) = await ExecuteAsync(Result<string>.Invalid("name", "The name is required."), Results.Ok);

        Assert.Equal(400, status);
        Assert.StartsWith("application/problem+json", contentType);
        Assert.Equal("The name is required.", body.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task ABareNotFoundFromAnyEndpoint_StillGetsAProblemDetailsBody()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AnAgentRunWithoutAGoal_IsAValidationProblemNamingTheField()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/agent/runs", new { goal = " ", profileId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("goal", out _));
    }

    [Fact]
    public async Task AnUnknownAgentRun_IsANotFoundProblemWithADetail()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/agent/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("That agent run does not exist.", body.GetProperty("detail").GetString());
    }

    private async Task<(int Status, string? ContentType, JsonElement Body)> ExecuteAsync<T>(Result<T> result, Func<T, IResult> ok)
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();

        await result.ToHttp(ok).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var body = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (context.Response.StatusCode, context.Response.ContentType, body);
    }
}
