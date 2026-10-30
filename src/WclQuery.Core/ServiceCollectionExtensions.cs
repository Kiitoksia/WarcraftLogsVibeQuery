using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WclQuery.Core.Engine;
using WclQuery.Core.Engine.Llm;
using WclQuery.Core.Knowledge;
using WclQuery.Core.WarcraftLogs;

namespace WclQuery.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Warcraft Logs client, glossary, conversations, query engine and the model provider chosen by
    /// <c>Llm:Provider</c> ("anthropic" or "azure-openai").
    /// </summary>
    public static IServiceCollection AddQueryEngine(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WarcraftLogsOptions>(config.GetSection(WarcraftLogsOptions.SectionName));
        services.Configure<QueryEngineOptions>(config.GetSection(QueryEngineOptions.SectionName));
        services.Configure<GlossaryOptions>(config.GetSection(GlossaryOptions.SectionName));
        services.Configure<AnthropicOptions>(config.GetSection(AnthropicOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(config.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<BlizzardOptions>(config.GetSection(BlizzardOptions.SectionName));
        services.AddHttpClient<EncounterJournal>();

        services.AddMemoryCache();
        services.AddHttpClient<WarcraftLogsClient>();
        services.AddSingleton<GlossaryStore>();
        services.AddSingleton<GlossaryAssistant>();
        services.AddSingleton<FightNotesStore>();
        services.AddSingleton<FightNotesWriter>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<QueryEngine>();

        var provider = config[$"{QueryEngineOptions.SectionName}:Provider"]?.Trim().ToLowerInvariant() ?? "anthropic";
        switch (provider)
        {
            case "azure-openai" or "azure" or "foundry":
                services.AddSingleton<ILlmProvider, AzureOpenAIProvider>();
                break;
            case "anthropic" or "claude":
                services.AddSingleton<ILlmProvider, AnthropicProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Llm:Provider '{provider}'. Use 'anthropic' or 'azure-openai'.");
        }
        return services;
    }
}
