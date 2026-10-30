using System.Text.Json.Serialization;

namespace WclQuery.Core.WarcraftLogs;

// Shapes mirror the Warcraft Logs v2 GraphQL schema (reportData.report).
// All timestamps are milliseconds relative to the report start.

public sealed class Report
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public Zone? Zone { get; set; }
    public List<Fight> Fights { get; set; } = [];
    public List<EncounterPhases> Phases { get; set; } = [];
    public MasterData MasterData { get; set; } = new();

    [JsonIgnore]
    public Dictionary<int, Actor> ActorsById { get; private set; } = [];

    [JsonIgnore]
    public Dictionary<long, Ability> AbilitiesById { get; private set; } = [];

    public void BuildLookups()
    {
        // The API sends null (not []) for lists that don't apply, e.g. phaseTransitions on trash fights,
        // and System.Text.Json writes that null over the property defaults. Normalise once here.
        Fights ??= [];
        Phases ??= [];
        MasterData ??= new();
        MasterData.Actors ??= [];
        MasterData.Abilities ??= [];
        foreach (var f in Fights)
        {
            f.FriendlyPlayers ??= [];
            f.EnemyNpcs ??= [];
            f.PhaseTransitions ??= [];
        }
        foreach (var p in Phases) p.Phases ??= [];

        ActorsById = MasterData.Actors.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        AbilitiesById = MasterData.Abilities.GroupBy(a => a.GameId).ToDictionary(g => g.Key, g => g.First());
    }

    public Fight? GetFight(int id) => Fights.FirstOrDefault(f => f.Id == id);

    public string ActorName(int? id)
    {
        if (id is null or -1) return "Environment";
        if (!ActorsById.TryGetValue(id.Value, out var actor)) return $"#{id}";
        if (actor.PetOwner is int owner && ActorsById.TryGetValue(owner, out var o))
            return $"{actor.Name} ({o.Name}'s pet)";
        return actor.Name;
    }

    public string AbilityName(long? id) =>
        id is null ? "" : AbilitiesById.TryGetValue(id.Value, out var a) ? a.Name : $"#{id}";

    public IReadOnlyList<PhaseInfo> PhasesFor(int encounterId) =>
        Phases.FirstOrDefault(p => p.EncounterId == encounterId)?.Phases ?? [];
}

public sealed class EncounterPhases
{
    public int EncounterId { get; set; }
    public List<PhaseInfo> Phases { get; set; } = [];
}

public sealed class PhaseInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsIntermission { get; set; }
}

public sealed class PhaseTransition
{
    /// <summary>Phase id (matches <see cref="PhaseInfo.Id"/>).</summary>
    public int Id { get; set; }
    /// <summary>Report-relative ms when the phase began.</summary>
    public long StartTime { get; set; }
}

public sealed class Zone
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Fight
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public int EncounterId { get; set; }
    public bool? Kill { get; set; }
    public int? Difficulty { get; set; }
    public double? BossPercentage { get; set; }
    public double? FightPercentage { get; set; }
    public int? Size { get; set; }
    public List<int> FriendlyPlayers { get; set; } = [];
    public List<FightNpc> EnemyNpcs { get; set; } = [];
    public List<PhaseTransition> PhaseTransitions { get; set; } = [];

    public bool IsBoss => EncounterId != 0;
    public long DurationMs => EndTime - StartTime;
}

public sealed class FightNpc
{
    public int Id { get; set; }
    public long GameId { get; set; }
    public int InstanceCount { get; set; }
}

public sealed class MasterData
{
    public List<Actor> Actors { get; set; } = [];
    public List<Ability> Abilities { get; set; } = [];
}

public sealed class Actor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Player, NPC, Pet</summary>
    public string Type { get; set; } = "";
    /// <summary>Class name for players (e.g. "Mage"), "Boss"/"NPC" for NPCs.</summary>
    public string SubType { get; set; } = "";
    public string? Server { get; set; }
    public int? PetOwner { get; set; }
    public long GameId { get; set; }
}

public sealed class Ability
{
    public long GameId { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; }
}

public sealed class EventPage
{
    public List<System.Text.Json.JsonElement> Data { get; set; } = [];
    public double? NextPageTimestamp { get; set; }
}

/// <summary>Arguments shared by the events and table GraphQL fields.</summary>
public sealed record ReportQuery
{
    public required long StartTime { get; init; }
    public required long EndTime { get; init; }
    public IReadOnlyList<int>? FightIds { get; init; }
    public required string DataType { get; init; }
    public string? HostilityType { get; init; }
    public long? AbilityId { get; init; }
    public int? SourceId { get; init; }
    public int? TargetId { get; init; }
    public string? FilterExpression { get; init; }
    public string? ViewBy { get; init; }
    /// <summary>Ignore all events after this many player deaths in each fight.</summary>
    public int? WipeCutoff { get; init; }
}
