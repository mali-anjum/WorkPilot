using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.AI.Providers;

/// <summary>
/// The innermost wrapper around a provider's client (spec 0006): turns a
/// final provider failure, after the SDK's own retries, into an
/// <see cref="AiProviderException"/> naming the purpose, provider, and model
/// (AC-6). Catches exactly an HTTP error status (<see cref="ClientResultException"/>),
/// a network failure (<see cref="HttpRequestException"/>), a cancellation the
/// caller never asked for (the per attempt timeout), and the SDK's
/// <see cref="AggregateException"/> ("Retry failed after N tries") when every
/// inner exception is one of those. A caller cancellation and any other
/// exception (a real bug) pass through untouched.
/// </summary>
internal sealed partial class ProviderErrorChatClient(IChatClient innerClient, ResolvedAiPurpose target, string? apiKey)
    : DelegatingChatClient(innerClient)
{
    private const int MaxMessageLength = 500;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw Translate(ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }

                update = updates.Current;
            }
            catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
            {
                throw Translate(ex);
            }

            yield return update;
        }
    }

    private static bool IsProviderFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        _ when cancellationToken.IsCancellationRequested => false,
        ClientResultException or HttpRequestException or OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 &&
            aggregate.InnerExceptions.All(inner => IsProviderFailure(inner, cancellationToken)),
        _ => false,
    };

    private static bool IsTimeout(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsTimeout),
        _ => false,
    };

    private AiProviderException Translate(Exception ex)
    {
        var detail = IsTimeout(ex)
            ? "The request timed out on every attempt."
            : (ex as AggregateException)?.InnerExceptions[^1].Message ?? ex.Message;
        var message = $"AI provider '{target.Provider}' (model {target.Model ?? "none"}) failed for purpose {target.Purpose}: {Redact(detail)}";
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        return new AiProviderException(target.Purpose, target.Provider, target.Model, message, ex);
    }

    // Providers echo a masked key back in 401 bodies (e.g. "sk-proj-****abcd");
    // scrub the configured key and anything key shaped so no part of it
    // reaches an audit row or a log (spec 0006, key invariants).
    private string Redact(string text)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            text = text.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return KeyLikePattern().Replace(text, "***");
    }

    [GeneratedRegex(@"\b(sk|AIza)[A-Za-z0-9_\-\*]{6,}")]
    private static partial Regex KeyLikePattern();
}
