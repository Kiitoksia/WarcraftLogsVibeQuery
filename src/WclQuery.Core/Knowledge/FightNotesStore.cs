using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WclQuery.Core.Knowledge;

/// <summary>
/// What the AI knows about one boss fight, condensed once from the Encounter Journal and editable by officers.
/// Source is "journal" (written by the AI from the Encounter Journal) or "edited" (changed by an editor since).
/// </summary>
public sealed record FightNote(
    int EncounterId,
    string Boss,
    string? Zone,
    string Text,
    string Source,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Something the AI learned in a conversation (usually the user's answer to a clarifying question) that it thinks
/// belongs in a boss's notes. Waits for an officer or editor to approve or dismiss it.
/// </summary>
public sealed record FightNoteSuggestion(
    string Id,
    int EncounterId,
    string Boss,
    string Text,
    string SuggestedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// Per-boss fight notes and pending suggestions, stored as one JSON file next to the glossary, with each previous
/// version kept in a history folder.
/// </summary>
public sealed class FightNotesStore
{
    public const int MaxNoteLength = 6_000;
    public const int MaxSuggestionLength = 400;
    private const int MaxPendingSuggestions = 50;
    private const string LearnedHeading = "**Learned from the guild's questions**";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly ILogger<FightNotesStore> _logger;
    private readonly Lock _lock = new();
    private Data? _data;

    public FightNotesStore(IOptions<GlossaryOptions> options, ILogger<FightNotesStore> logger)
    {
        _logger = logger;
        _directory = GlossaryStore.ResolveDirectory(options.Value);
    }

    private string FilePath => Path.Combine(_directory, "fight-notes.json");
    private string HistoryDirectory => Path.Combine(_directory, "fight-notes-history");

    public IReadOnlyList<FightNote> All()
    {
        lock (_lock) return [.. Load().Notes.Values.OrderBy(n => n.Zone).ThenBy(n => n.Boss)];
    }

    public FightNote? Get(int encounterId)
    {
        lock (_lock) return Load().Notes.GetValueOrDefault(encounterId);
    }

    public IReadOnlyList<FightNoteSuggestion> Suggestions()
    {
        lock (_lock) return [.. Load().Suggestions.OrderBy(s => s.CreatedAt)];
    }

    public FightNote Save(int encounterId, string boss, string? zone, string text, string source, string? updatedBy)
    {
        var note = new FightNote(encounterId, boss, zone, Normalize(text), source, updatedBy, DateTimeOffset.UtcNow);
        lock (_lock)
        {
            Load().Notes[encounterId] = note;
            Persist();
        }
        _logger.LogInformation("Fight notes for {Boss} ({Source}) saved by {User}", boss, source, updatedBy ?? "the AI");
        return note;
    }

    /// <summary>Adds a suggestion unless the same text is already waiting for that boss. Returns null when skipped.</summary>
    public FightNoteSuggestion? Suggest(int encounterId, string boss, string text, string suggestedBy)
    {
        text = text.Trim();
        if (text.Length is 0 or > MaxSuggestionLength) return null;
        lock (_lock)
        {
            var data = Load();
            if (data.Suggestions.Any(s => s.EncounterId == encounterId && string.Equals(s.Text, text, StringComparison.OrdinalIgnoreCase)))
                return null;
            if (data.Suggestions.Count >= MaxPendingSuggestions) return null;
            var suggestion = new FightNoteSuggestion(Guid.NewGuid().ToString("N")[..12], encounterId, boss, text, suggestedBy, DateTimeOffset.UtcNow);
            data.Suggestions.Add(suggestion);
            Persist();
            _logger.LogInformation("Fight note suggested for {Boss} by {User}: {Text}", boss, suggestedBy, text);
            return suggestion;
        }
    }

    /// <summary>
    /// Approves a suggestion: its text (or the editor's edited version) becomes a bullet at the end of the boss's
    /// notes. Returns the updated notes, or null if the suggestion no longer exists.
    /// </summary>
    public FightNote? Approve(string suggestionId, string? editedText, string approvedBy)
    {
        lock (_lock)
        {
            var data = Load();
            var suggestion = data.Suggestions.FirstOrDefault(s => s.Id == suggestionId);
            if (suggestion is null) return null;
            data.Suggestions.Remove(suggestion);

            var line = (string.IsNullOrWhiteSpace(editedText) ? suggestion.Text : editedText).Trim();
            if (!line.StartsWith("- ")) line = "- " + line;
            var existing = data.Notes.GetValueOrDefault(suggestion.EncounterId);
            var text = existing?.Text.TrimEnd() ?? "";
            if (!text.Contains(LearnedHeading)) text += (text.Length > 0 ? "\n\n" : "") + LearnedHeading;
            text += "\n" + line;
            var note = new FightNote(suggestion.EncounterId, existing?.Boss ?? suggestion.Boss, existing?.Zone,
                Normalize(text), "edited", approvedBy, DateTimeOffset.UtcNow);
            data.Notes[suggestion.EncounterId] = note;
            Persist();
            _logger.LogInformation("{User} approved a fight note for {Boss}: {Text}", approvedBy, note.Boss, line);
            return note;
        }
    }

    public bool Dismiss(string suggestionId)
    {
        lock (_lock)
        {
            var removed = Load().Suggestions.RemoveAll(s => s.Id == suggestionId) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim() + "\n";

    // Callers hold _lock.
    private Data Load()
    {
        if (_data is not null) return _data;
        try
        {
            if (File.Exists(FilePath))
                return _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Couldn't read the fight notes from {Directory}; starting empty", _directory);
        }
        return _data = new();
    }

    private void Persist()
    {
        Directory.CreateDirectory(HistoryDirectory);
        if (File.Exists(FilePath))
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Copy(FilePath, Path.Combine(HistoryDirectory, $"fight-notes-{stamp}.json"), overwrite: true);
        }
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, Json));
    }

    private sealed class Data
    {
        public Dictionary<int, FightNote> Notes { get; set; } = [];
        public List<FightNoteSuggestion> Suggestions { get; set; } = [];
    }
}
