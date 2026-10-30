using WclQuery.Core.Engine;
using WclQuery.Core.Engine.Llm;
using WclQuery.Core.WarcraftLogs;

namespace WclQuery.Core.Knowledge;

/// <summary>
/// Turns an editor's plain-English note ("shielded Malacrass = Veil of Twilight") into an updated glossary,
/// in the glossary's own format, looking up spell ids in a report when one is given. It only drafts:
/// the editor reviews the result and saves it themselves.
/// </summary>
public sealed class GlossaryAssistant(ILlmProvider llm, WarcraftLogsClient wcl)
{
    private const int MaxRounds = 6;
    private static readonly string[] LookupTools = ["get_report_overview", "find_abilities"];

    private const string Instructions = """
        You maintain a World of Warcraft raid guild's glossary: a Markdown document that tells an AI what the guild's
        informal names for boss mechanics mean in Warcraft Logs. An editor has written a note describing something to
        add or change. Apply it to the glossary.

        Format to keep:
        - Keep the existing introduction and every existing entry unless the note says to change or remove them.
        - One "## Boss name (Raid or dungeon name)" heading per boss. Add a heading if the boss isn't there yet.
        - One bullet per mechanic: **"what people call it", "other names"** = **Ability name** (debuff/buff/damage,
          spell id N), followed by any notes on how to count it (what counts as a mistake, what to filter, etc.).
          Sub-bullets are fine for details.
        - Rules that aren't about one ability (e.g. "damage to the shield means absorbed damage") go as a bullet
          under the relevant boss.

        Spell ids: if a report is available, use find_abilities (and get_report_overview for boss names) to look up
        the exact ability names and spell ids. Never invent an id; if you can't find one, write the ability name
        without an id.

        Reply with ONLY the complete updated glossary in Markdown: no commentary, no code fences.
        """;

    public async Task<(string Text, TokenUsage Usage)> DraftAsync(string currentGlossary, string note, string? reportCode,
        CancellationToken ct)
    {
        var usage = new TokenUsage.Accumulator(llm.Pricing);
        var session = llm.CreateSession();
        IReadOnlyList<ToolSpec> tools = reportCode is null
            ? []
            : [.. ReportTools.Definitions.Where(t => LookupTools.Contains(t.Name))];
        var reportTools = reportCode is null ? null : new ReportTools(wcl, reportCode, QuerySettings.None);

        session.AddUserText($"""
            Current glossary:
            <glossary>
            {currentGlossary}
            </glossary>

            Editor's note:
            <note>
            {note}
            </note>

            {(reportCode is null ? "No report is available for looking up spell ids." : "A report is available for looking up spell ids.")}
            """);

        for (var round = 0; round <= MaxRounds; round++)
        {
            var turn = await session.CompleteAsync([Instructions], tools, allowTools: tools.Count > 0 && round < MaxRounds, ct);
            usage.Add(turn.Usage);
            if (turn.ToolCalls.Count == 0)
                return (StripFences(turn.Text), usage.ToUsage());

            var results = new List<LlmToolResult>();
            foreach (var call in turn.ToolCalls)
            {
                if (reportTools is null || !LookupTools.Contains(call.Name))
                {
                    results.Add(new(call.Id, "Error: that tool isn't available here.", IsError: true));
                    continue;
                }
                try
                {
                    results.Add(new(call.Id, (await reportTools.ExecuteAsync(call.Name, call.Input, ct)).Text));
                }
                catch (Exception ex) when (ex is ToolInputException or WarcraftLogsException)
                {
                    results.Add(new(call.Id, $"Error: {ex.Message}", IsError: true));
                }
            }
            session.AddToolResults(results);
        }
        throw new InvalidOperationException("The AI didn't finish the draft. Try a shorter note.");
    }

    /// <summary>Some models wrap Markdown in ``` fences despite being asked not to.</summary>
    private static string StripFences(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```")) return text;
        var firstNewline = text.IndexOf('\n');
        var end = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && end > firstNewline ? text[(firstNewline + 1)..end].Trim() : text;
    }
}
