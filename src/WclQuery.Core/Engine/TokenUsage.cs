using Anthropic.Models.Messages;

namespace WclQuery.Core.Engine;

/// <summary>
/// Tokens used to answer one user message, split the way they're billed:
/// uncached input, cache reads (cheap), cache writes (slightly dearer than input) and output.
/// </summary>
public sealed record TokenUsage(long Input, long CacheRead, long CacheWrite, long Output, double? EstimatedCostUsd)
{
    public long TotalInput => Input + CacheRead + CacheWrite;

    // USD per million tokens: (input, output, cache read). Cache writes (5-minute TTL) cost 1.25x input.
    // Prices as of 2026-10; update when they change. Unknown models just don't get a cost estimate.
    private static readonly Dictionary<string, (double Input, double Output, double CacheRead)> Prices = new()
    {
        ["claude-opus-5-5"] = (4.00, 20.00, 0.20),
        ["claude-sonnet-5-5"] = (2.00, 10.00, 0.20),
        ["claude-haiku-5-5"] = (0.10, 0.50, 0.01),
    };

    public sealed class Accumulator(string model)
    {
        private long _input, _cacheRead, _cacheWrite, _output;

        public void Add(Usage usage)
        {
            _input += usage.InputTokens;
            _cacheRead += usage.CacheReadInputTokens ?? 0;
            _cacheWrite += usage.CacheCreationInputTokens ?? 0;
            _output += usage.OutputTokens;
        }

        public TokenUsage ToUsage()
        {
            double? cost = Prices.TryGetValue(model, out var p)
                ? (_input * p.Input + _cacheWrite * p.Input * 1.25 + _cacheRead * p.CacheRead + _output * p.Output) / 1_000_000
                : null;
            return new TokenUsage(_input, _cacheRead, _cacheWrite, _output, cost is double c ? Math.Round(c, 5) : null);
        }
    }
}
