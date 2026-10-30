using Anthropic.Models.Messages;
using Microsoft.Extensions.Caching.Memory;

namespace WclQuery.Core.Engine;

/// <summary>
/// One user's back-and-forth about a report: the full Claude message history, plus any tool results
/// held back while Claude waits for the user to answer a clarifying question.
/// </summary>
public sealed class Conversation
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string ReportCode { get; init; }

    public List<MessageParam> Messages { get; } = [];

    /// <summary>The ask_user tool call waiting for the user's reply, if any.</summary>
    public string? PendingQuestionToolUseId { get; set; }

    /// <summary>Results of other tools Claude called in the same turn as ask_user; sent along with the reply.</summary>
    public List<ContentBlockParam> PendingToolResults { get; } = [];

    /// <summary>Serializes requests so a double-click can't interleave two turns.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

public sealed class ConversationStore(IMemoryCache cache)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    public Conversation Create(string userId, string reportCode)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            ReportCode = reportCode,
        };
        cache.Set(Key(conversation.Id), conversation, new MemoryCacheEntryOptions { SlidingExpiration = Idle });
        return conversation;
    }

    /// <summary>Returns the conversation only if it exists and belongs to <paramref name="userId"/>.</summary>
    public Conversation? Get(string id, string userId) =>
        cache.TryGetValue(Key(id), out Conversation? c) && c!.UserId == userId ? c : null;

    private static string Key(string id) => $"conversation:{id}";
}
