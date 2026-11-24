using MeetingActions.Core.Abstractions;
using MeetingActions.Infrastructure.Integrations;
using MeetingActions.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure;

/// <summary>Registers infrastructure services selected by application configuration.</summary>
public static class DependencyInjection
{
    /// <summary>Registers the configured transcript-analysis provider and its health service.</summary>
    /// <exception cref="InvalidOperationException">The configured provider is not supported.</exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(options => options.Provider.Equals("Stub", StringComparison.OrdinalIgnoreCase)
                || options.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase),
                "Llm:Provider must be either 'Stub' or 'Ollama'.")
            .ValidateOnStart();
        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                "Ollama:BaseUrl must be a valid HTTP or HTTPS URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model),
                "Ollama:Model is required.")
            .Validate(options => options.NumCtx > 1500,
                "Ollama:NumCtx must exceed the 1500-token response reserve.")
            .Validate(options => options.Temperature is >= 0 and <= 2,
                "Ollama:Temperature must be between 0 and 2.")
            .Validate(options => options.RequestTimeoutSeconds > 0,
                "Ollama:RequestTimeoutSeconds must be positive.")
            .Validate(options => options.MaxTranscriptCharsPerChunk > 0,
                "Ollama:MaxTranscriptCharsPerChunk must be positive.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.KeepAlive),
                "Ollama:KeepAlive is required.")
            .ValidateOnStart();

        var provider = configuration[$"{LlmOptions.SectionName}:Provider"] ?? "Stub";
        if (provider.Equals("Stub", StringComparison.OrdinalIgnoreCase))
        {
            services.AddTransient<ILlmExtractor, StubLlmExtractor>();
            services.AddSingleton<ILlmHealth, StubLlmHealth>();
            return services;
        }

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            // Load the embedded prompt during service registration so a missing resource fails before serving requests.
            _ = OllamaExtractor.SystemPrompt;
            services.AddHttpClient(OllamaOptions.HttpClientName, (serviceProvider, client) =>
            {
                var settings = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(settings.BaseUrl, UriKind.Absolute);
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
            services.AddTransient<OllamaExtractor>();
            services.AddTransient<StubLlmExtractor>();
            services.AddTransient<FallbackLlmExtractor>();
            services.AddTransient<ILlmExtractor>(serviceProvider =>
                serviceProvider.GetRequiredService<IOptions<LlmOptions>>().Value.FallbackToStub
                    ? serviceProvider.GetRequiredService<FallbackLlmExtractor>()
                    : serviceProvider.GetRequiredService<OllamaExtractor>());
            services.AddSingleton<ILlmHealth, OllamaLlmHealth>();
            return services;
        }

        throw new InvalidOperationException(
            $"Unknown LLM provider '{provider}'. Supported providers are 'Stub' and 'Ollama'.");
    }
}
