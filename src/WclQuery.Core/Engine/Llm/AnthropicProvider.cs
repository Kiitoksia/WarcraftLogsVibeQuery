using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace WclQuery.Core.Engine.Llm;

public sealed class AnthropicOptions
{
    public const string SectionName = "Claude";

    /// <summary>Falls back to the ANTHROPIC_API_KEY environment variable when empty.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high (lower is cheaper and faster).</summary>
    public string Effort { get; set; } = "medium";
    /// <summary>Prompt-cache the conversation between rounds (the system prompt is always cached).</summary>
    public bool CacheConversation { get; set; } = true;
}

/// <summary>Claude via the Anthropic Messages API.</summary>
public sealed class AnthropicProvider(IOptions<AnthropicOptions> options) : ILlmProvider
{
    private readonly AnthropicOptions _options = options.Value;
    private readonly AnthropicClient _client = string.IsNullOrEmpty(options.Value.ApiKey)
        ? new()
        : new() { ApiKey = options.Value.ApiKey };

    // USD per million tokens, as of 2026-10. Update when prices change.
    private static readonly Dictionary<string, ModelPricing> Prices = new()
    {
        ["claude-opus-5-5"] = new(4.00, 20.00, 0.20),
        ["claude-sonnet-5-5"] = new(2.00, 10.00, 0.20),
        ["claude-haiku-5-5"] = new(0.10, 0.50, 0.01),
    };

    public string Name => $"anthropic/{_options.Model}";
    public ModelPricing? Pricing => Prices.GetValueOrDefault(_options.Model);
    public ILlmSession CreateSession() => new Session(_client, _options);

    private sealed class Session(AnthropicClient client, AnthropicOptions options) : ILlmSession
    {
        private readonly List<MessageParam> _messages = [];

        public bool IsEmpty => _messages.Count == 0;

        public void AddUserText(string text) => _messages.Add(new UserMessageParam { Content = text });

        public void AddToolResults(IReadOnlyList<LlmToolResult> results, string? note = null)
        {
            List<ContentBlockParam> content =
            [
                .. results.Select(r => (ContentBlockParam)new ToolResultBlockParam
                {
                    ToolUseID = r.ToolCallId,
                    Content = r.Content,
                    IsError = r.IsError,
                }),
            ];
            if (note is not null) content.Add(new TextBlockParam { Text = note });
            _messages.Add(new UserMessageParam { Content = content });
        }

        public async Task<LlmTurn> CompleteAsync(IReadOnlyList<string> systemParts, IReadOnlyList<ToolSpec> tools,
            bool allowTools, CancellationToken ct)
        {
            var request = new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 16000,
                // Cache breakpoint on the last system part caches the tools and everything before it.
                System = systemParts
                    .Select((text, i) => i == systemParts.Count - 1
                        ? new TextBlockParam { Text = text, CacheControl = new CacheControlEphemeral() }
                        : new TextBlockParam { Text = text })
                    .ToList(),
                OutputConfig = new OutputConfig { Effort = ParseEffort(options.Effort) },
                Messages = _messages,
            };
            // tool_choice is only allowed alongside tools.
            if (tools.Count > 0)
                request = request with
                {
                    Tools = [.. tools.Select(ToTool)],
                    ToolChoice = allowTools ? new ToolChoiceAuto() : new ToolChoiceNone(),
                };
            // Cache the conversation so far: each round resends the whole history, and cached tokens are far
            // cheaper than normal input. Only set when on: an explicit null would be sent to the API.
            if (options.CacheConversation)
                request = request with { CacheControl = new CacheControlEphemeral() };

            var response = await client.Messages.Create(request, ct);

            List<ContentBlockParam> assistant = [];
            var text = new List<string>();
            var calls = new List<LlmToolCall>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? t))
                {
                    assistant.Add(new TextBlockParam { Text = t.Text });
                    text.Add(t.Text);
                }
                else if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    // Signatures must be passed back unchanged.
                    assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    assistant.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    calls.Add(new LlmToolCall(toolUse.ID, toolUse.Name, toolUse.Input));
                }
            }
            _messages.Add(new AssistantMessageParam { Content = assistant });

            var stop = response.StopReason == "refusal" ? LlmStop.Refusal
                : response.StopReason == "max_tokens" ? LlmStop.MaxTokens
                : response.StopReason == "tool_use" ? LlmStop.ToolUse
                : LlmStop.EndTurn;
            var u = response.Usage;
            var usage = new LlmUsage(u.InputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0, u.OutputTokens);
            return new LlmTurn(string.Join("\n\n", text).Trim(), calls, stop, usage);
        }

        private static Tool ToTool(ToolSpec spec) => new()
        {
            Name = spec.Name,
            Description = spec.Description,
            InputSchema = new()
            {
                Properties = (spec.InputSchema["properties"]?.AsObject() ?? [])
                    .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                Required = spec.InputSchema["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToList() ?? [],
            },
        };

        private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
        {
            "low" => Effort.Low,
            "high" => Effort.High,
            _ => Effort.Medium,
        };
    }
}
