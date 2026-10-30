using Microsoft.Extensions.Caching.Memory;
using WclQuery.Core.Engine.Llm;

namespace WclQuery.Core.Engine;

/// <summary>
/// One user's back-and-forth about a report: the model session (which holds the message history),
/// plus any tool results held back while the model waits for the user to answer a clarifying question.
/// </summary>
public sealed class Conversation
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    /// <summary>Display name, credited on fight note suggestions.</summary>
    public required string UserName { get; init; }
    public required string ReportCode { get; init; }
    public required ILlmSession Session { get; init; }

    /// <summary>Filters in effect for the latest message (the user can change them between messages).</summary>
    public QuerySettings Settings { get; set; } = QuerySettings.None;

    /// <summary>The ask_user tool call waiting for the user's reply, if any.</summary>
    public string? PendingQuestionToolCallId { get; set; }

    /// <summary>Results of other tools called in the same turn as ask_user; sent along with the reply.</summary>
    public List<LlmToolResult> PendingToolResults { get; } = [];

    /// <summary>The user has answered a clarifying question, so the model may suggest what it learned as a fight note.</summary>
    public bool HasClarification { get; set; }

    /// <summary>Serializes requests so a double-click can't interleave two turns.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

public sealed class ConversationStore(IMemoryCache cache)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    public Conversation Create(string userId, string userName, string reportCode, ILlmSession session)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserName = userName,
            ReportCode = reportCode,
            Session = session,
        };
        cache.Set(Key(conversation.Id), conversation, new MemoryCacheEntryOptions { SlidingExpiration = Idle });
        return conversation;
    }

    /// <summary>Returns the conversation only if it exists and belongs to <paramref name="userId"/>.</summary>
    public Conversation? Get(string id, string userId) =>
        cache.TryGetValue(Key(id), out Conversation? c) && c!.UserId == userId ? c : null;

    private static string Key(string id) => $"conversation:{id}";
}
