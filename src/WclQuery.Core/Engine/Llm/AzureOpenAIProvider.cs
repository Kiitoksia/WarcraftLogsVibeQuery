using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace WclQuery.Core.Engine.Llm;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>The Foundry / Azure OpenAI resource endpoint, e.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>The deployment name in Foundry (not necessarily the model name).</summary>
    public string Deployment { get; set; } = "";
    /// <summary>minimal | low | medium | high, or empty to leave it to the model's default.</summary>
    public string ReasoningEffort { get; set; } = "low";

    // Optional prices (USD per million tokens) for the cost estimate shown under answers.
    public double? InputPricePerMTok { get; set; }
    public double? OutputPricePerMTok { get; set; }
    public double? CachedInputPricePerMTok { get; set; }
}

/// <summary>
/// GPT models deployed on Microsoft Foundry / Azure OpenAI, via the Responses API. Newer reasoning models only
/// allow function tools together with reasoning on the Responses API (not Chat Completions).
/// The conversation is kept server-side: each call chains from the previous response id and sends only new items.
/// </summary>
public sealed class AzureOpenAIProvider : ILlmProvider
{
    private readonly AzureOpenAIOptions _options;
#pragma warning disable OPENAI001 // the Responses API is marked experimental in the SDK
    private readonly ResponsesClient _responses;
#pragma warning restore OPENAI001

    public AzureOpenAIProvider(IOptions<AzureOpenAIOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.Deployment))
            throw new InvalidOperationException("AzureOpenAI:Endpoint, AzureOpenAI:ApiKey and AzureOpenAI:Deployment must be set.");

        var client = new AzureOpenAIClient(new Uri(_options.Endpoint), new ApiKeyCredential(_options.ApiKey));
#pragma warning disable OPENAI001
        _responses = client.GetResponsesClient();
#pragma warning restore OPENAI001
    }

    public string Name => $"azure-openai/{_options.Deployment}";

    public ModelPricing? Pricing =>
        _options is { InputPricePerMTok: double i, OutputPricePerMTok: double o }
            ? new ModelPricing(i, o, _options.CachedInputPricePerMTok ?? i, CacheWriteMultiplier: 1)
            : null;

    public ILlmSession CreateSession() => new Session(_responses, _options);

#pragma warning disable OPENAI001
    private sealed class Session(ResponsesClient responses, AzureOpenAIOptions options) : ILlmSession
    {
        // Items added since the last response; the server already has everything before that.
        private readonly List<ResponseItem> _pending = [];
        private string? _lastResponseId;

        public bool IsEmpty => _lastResponseId is null && _pending.Count == 0;

        public void AddUserText(string text) => _pending.Add(ResponseItem.CreateUserMessageItem(text));

        public void AddToolResults(IReadOnlyList<LlmToolResult> results, string? note = null)
        {
            foreach (var r in results) _pending.Add(ResponseItem.CreateFunctionCallOutputItem(r.ToolCallId, r.Content));
            if (note is not null) _pending.Add(ResponseItem.CreateUserMessageItem(note));
        }

        public async Task<LlmTurn> CompleteAsync(IReadOnlyList<string> systemParts, IReadOnlyList<ToolSpec> tools,
            bool allowTools, CancellationToken ct)
        {
            var request = new CreateResponseOptions
            {
                Model = options.Deployment,
                // Instructions aren't carried over between chained responses, so send them every time.
                Instructions = string.Join("\n\n", systemParts),
                PreviousResponseId = _lastResponseId,
                MaxOutputTokenCount = 16000,
                ToolChoice = allowTools ? ResponseToolChoice.CreateAutoChoice() : ResponseToolChoice.CreateNoneChoice(),
            };
            foreach (var item in _pending) request.InputItems.Add(item);
            foreach (var t in tools)
                request.Tools.Add(ResponseTool.CreateFunctionTool(t.Name, BinaryData.FromString(t.InputSchema.ToJsonString()),
                    strictModeEnabled: false, functionDescription: t.Description));
            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
                request.ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = new ResponseReasoningEffortLevel(options.ReasoningEffort.ToLowerInvariant()),
                };

            ResponseResult response = await responses.CreateResponseAsync(request, ct);
            _lastResponseId = response.Id;
            _pending.Clear();

            var calls = new List<LlmToolCall>();
            var refused = false;
            foreach (var item in response.OutputItems)
            {
                if (item is FunctionCallResponseItem call)
                    calls.Add(new LlmToolCall(call.CallId, call.FunctionName, ToolInput.Parse(call.FunctionArguments.ToString())));
                else if (item is MessageResponseItem message && message.Content.Any(p => p.Refusal is not null))
                    refused = true;
            }

            var stop = refused ? LlmStop.Refusal
                : calls.Count > 0 ? LlmStop.ToolUse
                : response.IncompleteStatusDetails?.Reason is not null ? LlmStop.MaxTokens
                : LlmStop.EndTurn;
            var u = response.Usage;
            var cached = u?.InputTokenDetails?.CachedTokenCount ?? 0;
            var usage = new LlmUsage((u?.InputTokenCount ?? 0) - cached, cached, 0, u?.OutputTokenCount ?? 0);
            return new LlmTurn((response.GetOutputText() ?? "").Trim(), calls, stop, usage);
        }
    }
#pragma warning restore OPENAI001
}
