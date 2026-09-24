using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WorkPilot.Api.Tests;

/// <summary>One scripted reply from <see cref="StubOpenAiServer"/>.</summary>
/// <param name="Status">HTTP status to return.</param>
/// <param name="Content">The assistant message text for a 200 reply.</param>
/// <param name="Delay">How long to wait before replying (to trip a client timeout).</param>
/// <param name="RetryAfter">A <c>Retry-After</c> header value, e.g. <c>"0"</c>.</param>
/// <param name="ErrorBody">A raw body for an error reply.</param>
internal sealed record StubReply(int Status = 200, string? Content = null, TimeSpan? Delay = null, string? RetryAfter = null, string? ErrorBody = null);

/// <summary>What the stub saw for one request.</summary>
internal sealed record StubRequest(string Path, string? Authorization, string? Model, string Body);

/// <summary>
/// A real Kestrel server on a random loopback port that speaks the OpenAI
/// Chat Completions format (spec 0006's "local fake HTTP endpoint"), so tests
/// exercise the real OpenAI adapter, config binding, retries, and timeouts
/// offline and for free. Replies follow <c>script</c> in order; once it runs
/// out, every request gets <c>fallback</c>.
/// </summary>
internal sealed class StubOpenAiServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentQueue<StubReply> script;
    private readonly StubReply fallback;

    private StubOpenAiServer(WebApplication app, IEnumerable<StubReply> script, StubReply fallback)
    {
        this.app = app;
        this.script = new ConcurrentQueue<StubReply>(script);
        this.fallback = fallback;
    }

    /// <summary>Every request received, in order.</summary>
    public ConcurrentQueue<StubRequest> Requests { get; } = new();

    /// <summary>The OpenAI compatible base URL to put in <c>Ai:Providers:&lt;name&gt;:Endpoint</c>.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>A stub that always answers 200 with <paramref name="content"/>.</summary>
    public static Task<StubOpenAiServer> StartAsync(string content) => StartAsync([], new StubReply(Content: content));

    /// <summary>A stub that plays <paramref name="script"/>, then answers <paramref name="fallback"/>.</summary>
    public static async Task<StubOpenAiServer> StartAsync(IEnumerable<StubReply> script, StubReply fallback)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        var server = new StubOpenAiServer(app, script, fallback);
        app.Map("/{**path}", server.HandleAsync);
        await app.StartAsync();

        var address = app.Urls.First(); // the real bound port, once started
        server.Endpoint = $"{address}/v1";
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        string? model = null;
        try
        {
            model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        Requests.Enqueue(new StubRequest(context.Request.Path, context.Request.Headers.Authorization.FirstOrDefault(), model, body));

        var reply = script.TryDequeue(out var next) ? next : fallback;
        if (reply.Delay is { } delay)
        {
            try
            {
                await Task.Delay(delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        context.Response.StatusCode = reply.Status;
        if (reply.RetryAfter is not null)
        {
            context.Response.Headers.RetryAfter = reply.RetryAfter;
        }

        context.Response.ContentType = "application/json";
        if (reply.Status != 200)
        {
            await context.Response.WriteAsync(reply.ErrorBody ?? """{"error":{"message":"stub failure","type":"server_error"}}""");
            return;
        }

        var completion = new
        {
            id = "chatcmpl-stub",
            @object = "chat.completion",
            created = 1_700_000_000,
            model = model ?? "stub",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = reply.Content ?? string.Empty }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 },
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(completion));
    }
}
