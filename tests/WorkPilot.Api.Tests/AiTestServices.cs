using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkPilot.AI.Agent;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Agent;

namespace WorkPilot.Api.Tests;

/// <summary>
/// Builds the real <c>AddWorkPilotAi</c> registration from in memory
/// configuration, the same path the Api uses, so a test proves that a config
/// change alone reroutes a purpose (spec 0006, AC-1).
/// </summary>
internal static class AiTestServices
{
    public static ServiceProvider Build(IDictionary<string, string?> config, CapturingLoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });
        services.AddWorkPilotAi(configuration);
        services.AddScoped<IPlanner, ChatClientPlanner>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Config with one provider named <paramref name="provider"/> at <paramref name="endpoint"/> answering the Default purpose.</summary>
    public static Dictionary<string, string?> SingleProvider(string provider, string endpoint, string model, string? apiKey = "sk-test-key", int? timeoutSeconds = null)
    {
        var config = new Dictionary<string, string?>
        {
            [$"Ai:Providers:{provider}:Endpoint"] = endpoint,
            ["Ai:Purposes:Default:Provider"] = provider,
            ["Ai:Purposes:Default:Model"] = model,
        };

        // Only set keys that have a value: a null entry binds as 0 or "", not "absent".
        if (apiKey is not null)
        {
            config[$"Ai:Providers:{provider}:ApiKey"] = apiKey;
        }

        if (timeoutSeconds is not null)
        {
            config[$"Ai:Providers:{provider}:TimeoutSeconds"] = timeoutSeconds.ToString();
        }

        return config;
    }
}

/// <summary>Collects every formatted log message, for asserting what was and wasn't logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
