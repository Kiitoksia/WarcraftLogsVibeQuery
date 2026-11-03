using System.ClientModel;
using Anthropic.Exceptions;

namespace WclQuery.Core.Engine.Llm;

/// <summary>Retry and timeout settings shared by the model providers.</summary>
public static class LlmLimits
{
    public const int MaxRetries = 5;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
}

/// <summary>A model call that failed, described for the user, with the HTTP status to answer with.</summary>
public sealed record ModelError(string Message, int HttpStatus, string Kind);

/// <summary>
/// Turns errors from the model service (Claude or Azure OpenAI) into a message that says what actually went wrong,
/// instead of a blanket "unavailable".
/// </summary>
public static class ModelErrors
{
    /// <summary>
    /// Null when <paramref name="ex"/> isn't a model service error. <paramref name="requestAborted"/> tells a timeout
    /// apart from the user closing the page.
    /// </summary>
    public static ModelError? Describe(Exception ex, bool requestAborted)
    {
        switch (ex)
        {
            case ClientResultException { Status: 0 } c: // no response at all: network failure or timeout
                return c.InnerException is OperationCanceledException or TimeoutException && !requestAborted ? TimedOut : Unreachable;
            case ClientResultException c:
                return FromStatus(c.Status, c.Message);
            case AnthropicApiException a:
                return FromStatus((int)a.StatusCode, a.ResponseBody ?? a.Message);
            case AnthropicIOException:
                return Unreachable;
            case OperationCanceledException or TimeoutException when !requestAborted:
                return TimedOut;
            case System.Net.Http.HttpRequestException:
                return Unreachable;
            default:
                return ex.InnerException is { } inner ? Describe(inner, requestAborted) : null;
        }
    }

    private static readonly ModelError TimedOut = new(
        "This took too long to answer. Try a narrower question (one pull or one phase).", 504, "timeout");

    private static readonly ModelError Unreachable = new(
        "Couldn't reach the AI service. Try again shortly.", 502, "unreachable");

    private static ModelError FromStatus(int status, string detail) => status switch
    {
        429 => new("The AI is busy right now (too many requests at once, even after retrying). Wait a minute and try again.",
            503, "rate-limit"),
        400 when detail.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
              || detail.Contains("content management policy", StringComparison.OrdinalIgnoreCase)
            => new("Azure's content filter blocked this question (WoW words like kill, death or bite sometimes trip it). Try rewording it.",
                400, "content-filter"),
        400 when detail.Contains("context", StringComparison.OrdinalIgnoreCase) && detail.Contains("length", StringComparison.OrdinalIgnoreCase)
            => new("This conversation has got too long for the AI. Start a new question.", 400, "too-long"),
        401 or 403 => new("The AI service rejected the server's key. An admin needs to check the AI settings.", 502, "auth"),
        404 => new("The AI model or deployment wasn't found. An admin needs to check the AI settings.", 502, "not-found"),
        408 or 504 => TimedOut,
        529 or >= 500 => new("The AI service had a problem on its side. Try again shortly.", 502, "service"),
        _ => new($"The AI service returned an error ({status}). Try again shortly.", 502, $"http-{status}"),
    };
}
