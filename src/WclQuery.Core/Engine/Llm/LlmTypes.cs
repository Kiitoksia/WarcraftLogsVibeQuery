using System.Text.Json;
using System.Text.Json.Nodes;

namespace WclQuery.Core.Engine.Llm;

// Provider-neutral shapes the query engine works with. Each provider (Anthropic, Azure OpenAI)
// translates these to and from its own SDK types and keeps its own native message history.

/// <summary>A tool the model can call. <see cref="InputSchema"/> is a JSON Schema object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

public sealed record LlmToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

public sealed record LlmToolResult(string ToolCallId, string Content, bool IsError = false);

/// <summary>Tokens for one model call, split the way they're billed.</summary>
public sealed record LlmUsage(long Input, long CacheRead, long CacheWrite, long Output);

public enum LlmStop { EndTurn, ToolUse, MaxTokens, Refusal }

/// <summary>One model response: any text, any tool calls, why it stopped, and what it cost.</summary>
public sealed record LlmTurn(string Text, IReadOnlyList<LlmToolCall> ToolCalls, LlmStop Stop, LlmUsage Usage);

/// <summary>USD per million tokens. Cache writes cost input × <see cref="CacheWriteMultiplier"/>.</summary>
public sealed record ModelPricing(double Input, double Output, double CacheRead, double CacheWriteMultiplier = 1.25);

public interface ILlmProvider
{
    /// <summary>Name for logs, e.g. "anthropic/claude-opus-5-5".</summary>
    string Name { get; }

    /// <summary>Prices for cost estimates; null when unknown (no estimate is shown).</summary>
    ModelPricing? Pricing { get; }

    /// <summary>Starts an empty conversation. The session keeps the provider's native message history.</summary>
    ILlmSession CreateSession();
}

public interface ILlmSession
{
    bool IsEmpty { get; }

    void AddUserText(string text);

    /// <summary>Answers every tool call from the last turn; <paramref name="note"/> is extra text for the model.</summary>
    void AddToolResults(IReadOnlyList<LlmToolResult> results, string? note = null);

    /// <summary>
    /// Calls the model. <paramref name="systemParts"/> are sent in order; the last one is the most likely to change
    /// (providers cache everything before it). With <paramref name="allowTools"/> false the model must answer in text.
    /// The model's reply is appended to the history.
    /// </summary>
    Task<LlmTurn> CompleteAsync(IReadOnlyList<string> systemParts, IReadOnlyList<ToolSpec> tools, bool allowTools,
        CancellationToken ct);
}

internal static class ToolInput
{
    /// <summary>Parses tool-call arguments JSON into a dictionary; malformed JSON gives an empty one.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }
}
