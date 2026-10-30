using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WclQuery.Core.Engine.Llm;

namespace WclQuery.Core.Knowledge;

/// <summary>
/// Writes a boss's fight notes once, by condensing its Encounter Journal entry, so later questions get the fight's
/// adds, abilities and quirks in a few hundred tokens instead of looking the journal up every time.
/// </summary>
public sealed class FightNotesWriter(
    ILlmProvider llm,
    EncounterJournal journal,
    FightNotesStore store,
    IMemoryCache cache,
    ILogger<FightNotesWriter> logger)
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    private const string Instructions = """
        You write compact fight notes for an AI that answers World of Warcraft raiders' questions about Warcraft Logs
        reports. You get one boss's entry from Blizzard's in-game Encounter Journal. Condense it into notes that help
        the AI work out what players mean when they talk about the fight informally, and how to look it up.

        Write Markdown, at most about 300 words:
        - One line on the fight's shape (single target, council, phases/intermissions and what triggers them).
        - **Adds and objects**: each creature or object players deal with, what it does, and the informal names
          players are likely to use for it (e.g. eggs, orbs, snakes, the big add). Note shared health, shields that
          absorb damage, and adds that spawn from something else.
        - **Abilities** by phase: one bullet each for the abilities players talk about (things that hit the raid,
          debuffs, soaks, pools, beams, waves, frontals, things to dodge or interrupt). Format:
          **Ability name** (spell id N): what it does in a few words; likely informal names in quotes.
        - Skip flavour text, role tags and abilities that only matter to the boss's own animation.
        Spell ids: copy them from the journal exactly; never invent one. The log sometimes records the damage under a
        different id from the journal's, so finish with the line: "_Journal spell ids; confirm the logged id with
        find_abilities._"
        Reply with ONLY the notes: no heading for the boss, no commentary, no code fences.
        """;

    /// <summary>
    /// Returns the boss's notes, writing them from the journal the first time. Null when the journal isn't set up or
    /// doesn't have the boss (that failure is remembered for a while so questions don't keep retrying).
    /// </summary>
    public async Task<(FightNote? Note, LlmUsage? Usage)> EnsureAsync(int encounterId, string boss, string? zone, CancellationToken ct)
    {
        if (store.Get(encounterId) is { } existing) return (existing, null);
        if (!journal.IsConfigured || cache.TryGetValue(FailedKey(encounterId), out _)) return (null, null);

        var gate = _locks.GetOrAdd(encounterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (store.Get(encounterId) is { } written) return (written, null); // another request wrote them meanwhile
            try
            {
                return await WriteAsync(encounterId, boss, zone, updatedBy: null, ct);
            }
            catch (EncounterJournalException ex)
            {
                logger.LogWarning("No fight notes for {Boss}: {Error}", boss, ex.Message);
                cache.Set(FailedKey(encounterId), true, TimeSpan.FromHours(1));
                return (null, null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>(Re)writes a boss's notes from the journal, replacing whatever is there.</summary>
    public async Task<(FightNote Note, LlmUsage Usage)> WriteAsync(int encounterId, string boss, string? zone,
        string? updatedBy, CancellationToken ct)
    {
        var entry = await journal.GetBossAsync(boss, zone, ct);
        var session = llm.CreateSession();
        session.AddUserText($"""
            Encounter Journal entry:
            <journal>
            {entry}
            </journal>
            """);
        var turn = await session.CompleteAsync([Instructions], [], allowTools: false, ct);

        var text = turn.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("The AI returned empty fight notes. Try again.");
        if (text.Length > FightNotesStore.MaxNoteLength) text = text[..FightNotesStore.MaxNoteLength];

        logger.LogInformation("Wrote fight notes for {Boss}: {Input} input + {Output} output tokens", boss, turn.Usage.Input, turn.Usage.Output);
        return (store.Save(encounterId, boss, zone, text, "journal", updatedBy), turn.Usage);
    }

    private static string FailedKey(int encounterId) => $"fightnotes:failed:{encounterId}";
}
