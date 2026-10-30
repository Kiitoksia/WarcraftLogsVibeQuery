using WclQuery.Core.Engine.Llm;

namespace WclQuery.Core.Engine;

/// <summary>
/// Tokens used to answer one user message, split the way they're billed:
/// uncached input, cache reads (cheap), cache writes (slightly dearer than input) and output.
/// </summary>
public sealed record TokenUsage(long Input, long CacheRead, long CacheWrite, long Output, double? EstimatedCostUsd)
{
    public long TotalInput => Input + CacheRead + CacheWrite;

    public sealed class Accumulator(ModelPricing? pricing)
    {
        private long _input, _cacheRead, _cacheWrite, _output;

        public void Add(LlmUsage usage)
        {
            _input += usage.Input;
            _cacheRead += usage.CacheRead;
            _cacheWrite += usage.CacheWrite;
            _output += usage.Output;
        }

        public TokenUsage ToUsage()
        {
            double? cost = pricing is { } p
                ? (_input * p.Input + _cacheWrite * p.Input * p.CacheWriteMultiplier + _cacheRead * p.CacheRead + _output * p.Output) / 1_000_000
                : null;
            return new TokenUsage(_input, _cacheRead, _cacheWrite, _output, cost is double c ? Math.Round(c, 5) : null);
        }
    }
}
