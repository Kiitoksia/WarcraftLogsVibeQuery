using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace WclQuery.Core.Engine.Llm;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>The Foundry / Azure OpenAI resource endpoint, e.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>The deployment name in Foundry (not necessarily the model name).</summary>
    public string Deployment { get; set; } = "";
    /// <summary>low | medium | high, or empty to leave it to the model's default.</summary>
    public string ReasoningEffort { get; set; } = "low";

    // Optional prices (USD per million tokens) for the cost estimate shown under answers.
    public double? InputPricePerMTok { get; set; }
    public double? OutputPricePerMTok { get; set; }
    public double? CachedInputPricePerMTok { get; set; }
}

/// <summary>GPT models deployed on Microsoft Foundry / Azure OpenAI, via Chat Completions with function tools.</summary>
public sealed class AzureOpenAIProvider : ILlmProvider
{
    private readonly AzureOpenAIOptions _options;
    private readonly ChatClient _chat;

    public AzureOpenAIProvider(IOptions<AzureOpenAIOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.Deployment))
            throw new InvalidOperationException("AzureOpenAI:Endpoint, AzureOpenAI:ApiKey and AzureOpenAI:Deployment must be set.");

        var client = new AzureOpenAIClient(new Uri(_options.Endpoint), new ApiKeyCredential(_options.ApiKey));
        _chat = client.GetChatClient(_options.Deployment);
    }

    public string Name => $"azure-openai/{_options.Deployment}";

    public ModelPricing? Pricing =>
        _options is { InputPricePerMTok: double i, OutputPricePerMTok: double o }
            ? new ModelPricing(i, o, _options.CachedInputPricePerMTok ?? i, CacheWriteMultiplier: 1)
            : null;

    public ILlmSession CreateSession() => new Session(_chat, _options);

    private sealed class Session(ChatClient chat, AzureOpenAIOptions options) : ILlmSession
    {
        private readonly List<ChatMessage> _messages = [];

        public bool IsEmpty => _messages.Count == 0;

        public void AddUserText(string text) => _messages.Add(new UserChatMessage(text));

        public void AddToolResults(IReadOnlyList<LlmToolResult> results, string? note = null)
        {
            // Every tool call must be answered, in a tool message, before anything else is added.
            foreach (var r in results) _messages.Add(new ToolChatMessage(r.ToolCallId, r.Content));
            if (note is not null) _messages.Add(new UserChatMessage(note));
        }

        public async Task<LlmTurn> CompleteAsync(IReadOnlyList<string> systemParts, IReadOnlyList<ToolSpec> tools,
            bool allowTools, CancellationToken ct)
        {
            // Azure OpenAI caches stable prompt prefixes automatically, so keep the system message first and unchanged.
            List<ChatMessage> messages = [new SystemChatMessage(string.Join("\n\n", systemParts)), .. _messages];

            var chatOptions = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 16000,
                ToolChoice = allowTools ? ChatToolChoice.CreateAutoChoice() : ChatToolChoice.CreateNoneChoice(),
            };
            foreach (var t in tools)
                chatOptions.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.InputSchema.ToJsonString())));
#pragma warning disable AOAI001, OPENAI001 // newer-model options are marked experimental in the SDK
            // GPT-5-era models reject max_tokens and need max_completion_tokens.
            chatOptions.SetNewMaxCompletionTokensPropertyEnabled(true);
            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
                chatOptions.ReasoningEffortLevel = new ChatReasoningEffortLevel(options.ReasoningEffort.ToLowerInvariant());
#pragma warning restore AOAI001, OPENAI001

            ChatCompletion completion = await chat.CompleteChatAsync(messages, chatOptions, ct);
            _messages.Add(new AssistantChatMessage(completion));

            var text = string.Join("", completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text)).Trim();
            var calls = completion.ToolCalls
                .Select(c => new LlmToolCall(c.Id, c.FunctionName, ToolInput.Parse(c.FunctionArguments.ToString())))
                .ToList();

            var stop = completion.FinishReason switch
            {
                ChatFinishReason.ToolCalls => LlmStop.ToolUse,
                ChatFinishReason.Length => LlmStop.MaxTokens,
                ChatFinishReason.ContentFilter => LlmStop.Refusal,
                _ => calls.Count > 0 ? LlmStop.ToolUse : LlmStop.EndTurn,
            };
            var u = completion.Usage;
            var cached = u?.InputTokenDetails?.CachedTokenCount ?? 0;
            var usage = new LlmUsage((u?.InputTokenCount ?? 0) - cached, cached, 0, u?.OutputTokenCount ?? 0);
            return new LlmTurn(text, calls, stop, usage);
        }
    }
}
