using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkPilot.AI.Providers;

namespace WorkPilot.Api.Tests;

// Spec 0006, AC-4: invalid AI config fails startup with one message naming
// every problem, and never echoes a key. Pure unit tests over the validator,
// plus one real host start to prove ValidateOnStart is wired.
public class AiOptionsValidatorTests
{
    private const string PlantedKey = "sk-test-PLANTED-9f8e7d6c5b";

    [Fact]
    public void Collect_WithTheCommittedDefault_IsValid()
    {
        // covers AC-3: Default -> Fake plus keyless presets validates with no key anywhere.
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ai:Providers:openai:Endpoint"] = "https://api.openai.com/v1",
            ["Ai:Providers:gemini:Endpoint"] = "https://generativelanguage.googleapis.com/v1beta/openai/",
            ["Ai:Purposes:Default:Provider"] = "Fake",
        });

        Assert.Empty(AiOptionsValidator.Collect(options));
    }

    [Fact]
    public void Collect_WithAValidRealProvider_IsValid()
    {
        var options = Bind(Valid());

        Assert.Empty(AiOptionsValidator.Collect(options));
    }

    [Fact]
    public void Collect_WithNoDefaultPurpose_NamesIt()
    {
        var config = Valid();
        config.Remove("Ai:Purposes:Default:Provider");
        config.Remove("Ai:Purposes:Default:Model");
        config["Ai:Purposes:Planner:Provider"] = "deepseek";
        config["Ai:Purposes:Planner:Model"] = "deepseek-chat";

        AssertSingleError(config, "Ai:Purposes:Default is not set");
    }

    [Fact]
    public void Collect_WithAnUnknownPurposeName_NamesIt()
    {
        var config = Valid();
        config["Ai:Purposes:Planer:Provider"] = "deepseek";

        AssertSingleError(config, "Ai:Purposes:Planer is not a known purpose");
    }

    [Fact]
    public void Collect_WithAnUnknownProvider_NamesIt()
    {
        var config = Valid();
        config["Ai:Purposes:Default:Provider"] = "anthropic";

        AssertSingleError(config, "Ai:Purposes:Default:Provider is 'anthropic'");
    }

    [Fact]
    public void Collect_WithARealProviderButNoModel_NamesIt()
    {
        var config = Valid();
        config.Remove("Ai:Purposes:Default:Model");

        AssertSingleError(config, "Ai:Purposes:Default:Model is not set");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://api.deepseek.com/v1")]
    [InlineData("/v1")]
    public void Collect_WithABadEndpoint_NamesIt(string endpoint)
    {
        var config = Valid();
        config["Ai:Providers:deepseek:Endpoint"] = endpoint;

        AssertSingleError(config, "Ai:Providers:deepseek:Endpoint must be an absolute http or https URL");
    }

    [Fact]
    public void Collect_WithAMissingKeyOnAUsedProvider_SaysWhereToPutIt()
    {
        var config = Valid();
        config.Remove("Ai:Providers:deepseek:ApiKey");

        AssertSingleError(config, "Ai:Providers:deepseek:ApiKey is not set");
    }

    [Fact]
    public void Collect_WithAKeylessProviderFlagged_NeedsNoKey()
    {
        var config = Valid();
        config.Remove("Ai:Providers:deepseek:ApiKey");
        config["Ai:Providers:deepseek:RequiresApiKey"] = "false";

        Assert.Empty(AiOptionsValidator.Collect(Bind(config)));
    }

    [Fact]
    public void Collect_WithAnUnusedProviderMissingItsKey_IsValid()
    {
        var config = Valid();
        config["Ai:Providers:openai:Endpoint"] = "https://api.openai.com/v1";

        Assert.Empty(AiOptionsValidator.Collect(Bind(config)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Collect_WithTimeoutOutOfRange_NamesIt(int timeout)
    {
        var config = Valid();
        config["Ai:Providers:deepseek:TimeoutSeconds"] = timeout.ToString();

        AssertSingleError(config, $"Ai:Providers:deepseek:TimeoutSeconds is {timeout}");
    }

    [Fact]
    public void Collect_WithFakeDeclaredAsAProvider_SaysItIsReserved()
    {
        var config = Valid();
        config["Ai:Providers:Fake:Endpoint"] = "https://example.com/v1";

        AssertSingleError(config, "Ai:Providers:Fake is reserved");
    }

    [Fact]
    public void Collect_WithSeveralProblems_ListsEveryOne_AndNeverTheKey()
    {
        var config = Valid();
        config["Ai:Providers:deepseek:Endpoint"] = "nope";
        config["Ai:Providers:deepseek:TimeoutSeconds"] = "0";
        config["Ai:Purposes:Planer:Provider"] = "deepseek";

        var errors = AiOptionsValidator.Collect(Bind(config));

        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.DoesNotContain(PlantedKey, e));
    }

    [Fact]
    public async Task HostStart_WithInvalidConfig_FailsBeforeServing()
    {
        // covers AC-4: ValidateOnStart stops the host, not a later background job.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Purposes:Default:Provider"] = "deepseek",
            ["Ai:Purposes:Default:Model"] = "deepseek-chat",
            ["Ai:Providers:deepseek:Endpoint"] = "https://api.deepseek.com/v1",
        });
        builder.Services.AddWorkPilotAi(builder.Configuration);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Ai:Providers:deepseek:ApiKey is not set", ex.Message);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Ai:Providers:deepseek:Endpoint"] = "https://api.deepseek.com/v1",
        ["Ai:Providers:deepseek:ApiKey"] = PlantedKey,
        ["Ai:Purposes:Default:Provider"] = "deepseek",
        ["Ai:Purposes:Default:Model"] = "deepseek-chat",
    };

    private static void AssertSingleError(Dictionary<string, string?> config, string expected)
    {
        var error = Assert.Single(AiOptionsValidator.Collect(Bind(config)));
        Assert.Contains(expected, error);
        Assert.DoesNotContain(PlantedKey, error);
    }

    private static AiOptions Bind(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddOptions<AiOptions>().Bind(new ConfigurationBuilder().AddInMemoryCollection(config).Build().GetSection(AiOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<AiOptions>>().Value;
    }
}
